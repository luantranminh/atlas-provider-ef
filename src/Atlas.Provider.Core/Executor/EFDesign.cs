using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Provider.Core.Executor;

// EFDesign is a class that provides a way to execute Entity Framework Core design-time operations.
// It is used by the AtlasProviderEF to execute EF Core commands.
internal class EFDesign : IDisposable
{
  // The name of the assembly that contains the design-time commands.
  private const string DesignAssemblyName = "Microsoft.EntityFrameworkCore.Design";
  // The fully qualified name of the report handler type.
  private const string ReportHandlerTypeName = DesignAssemblyName + ".OperationReportHandler";
  // The fully qualified name of the result handler type.
  private const string ResultHandlerTypeName = DesignAssemblyName + ".OperationResultHandler";
  // The fully qualified name of the executor type.
  private const string ExecutorTypeName = DesignAssemblyName + ".OperationExecutor";

  // All the operation types we are interested in.
  private const string GetContextTypesTypeName = ExecutorTypeName + "+GetContextTypes";
  private const string ScriptDbContextTypeName = ExecutorTypeName + "+ScriptDbContext";
  private const string GetContextInfoTypeName = ExecutorTypeName + "+GetContextInfo";

  private readonly Assembly _commandsAssembly;
  private readonly object _executor;
  private readonly Type _resultHandlerType;
  private readonly string _appBasePath;
  private readonly string _projectDir;

  public EFDesign(
    string assembly,
    string? startupAssembly,
    string? projectDir,
    string? dataDirectory,
    string? rootNamespace,
    string? language,
    bool nullable,
    string[]? remainingArguments
  )
  {
    try
    {
      _commandsAssembly = Assembly.Load(new AssemblyName { Name = DesignAssemblyName });
    }
    catch (FileNotFoundException ex)
    when (ex.FileName != null &&
      new AssemblyName(ex.FileName).Name == DesignAssemblyName)
    {
      throw new FileNotFoundException(
        $"Could not find package {DesignAssemblyName}. " +
        $"This package is required for the tool to work. Ensure your startup project is correct, install the package, and try again."
      );
    }

    var reportHandlerType = _commandsAssembly.GetType(ReportHandlerTypeName, throwOnError: true, ignoreCase: false)!;
    var reportHandler = Activator.CreateInstance(
      reportHandlerType,
      (Action<string>)(_ => { }),
      (Action<string>)(_ => { }),
      (Action<string>)(_ => { }),
      (Action<string>)(_ => { })
    )!;

    var assemblyFileName = Path.GetFileNameWithoutExtension(assembly);
    var startupAssemblyFileName = startupAssembly == null
        ? assemblyFileName
        : Path.GetFileNameWithoutExtension(startupAssembly);

    _executor = Activator.CreateInstance(
      _commandsAssembly.GetType(ExecutorTypeName, throwOnError: true, ignoreCase: false)!,
      reportHandler,
      new Dictionary<string, object?>
      {
        { "targetName", assemblyFileName },
        { "startupTargetName", startupAssemblyFileName },
        { "projectDir", projectDir ?? Directory.GetCurrentDirectory() },
        { "rootNamespace", rootNamespace ?? assemblyFileName },
        { "language", language },
        { "nullable", nullable },
        { "remainingArguments", remainingArguments ?? Array.Empty<string>() }
      })!;
    _resultHandlerType = _commandsAssembly.GetType(ResultHandlerTypeName, throwOnError: true, ignoreCase: false)!;

    // Setup the assembly resolution handler for the design-time commands.
    var configurationFile = (startupAssembly ?? assembly) + ".config";
    if (File.Exists(configurationFile))
    {
      AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", configurationFile);
    }
    if (dataDirectory != null)
    {
      AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectory);
    }
    _appBasePath = Path.GetFullPath(Path.Combine(
      Directory.GetCurrentDirectory(),
      Path.GetDirectoryName(startupAssembly ?? assembly)!
    ));
    _projectDir = projectDir ?? Directory.GetCurrentDirectory();
    
    AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
  }

  public void Dispose()
  {
    AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
  }

  public IEnumerable<IDictionary> GetContextTypes()
    => InvokeOperation<IEnumerable<IDictionary>>(GetContextTypesTypeName,
      new Dictionary<string, object>(0));

  public IDictionary GetContextInfo(string? name)
    => InvokeOperation<IDictionary>(GetContextInfoTypeName,
      new Dictionary<string, object?> { ["contextType"] = name });

  public string ScriptDbContext(string? name)
    => InvokeOperation<string>(ScriptDbContextTypeName,
      new Dictionary<string, object?> { ["contextType"] = name });

  /// <summary>
  /// Gets entity and table information from the EF Core model
  /// </summary>
  /// <param name="contextName">The DbContext name</param>
  /// <returns>List of entity and table information</returns>
  public List<(string EntityName, string TableName)> GetEntityTableInfo(string? contextName)
  {
    var executorType = _executor.GetType();
    var dbContextOperations = GetDbContextOperations(executorType);
    
    if (dbContextOperations == null)
      throw new InvalidOperationException("Could not access DbContext operations");

    var createContextMethod = dbContextOperations.GetType().GetMethod("CreateContext", new[] { typeof(string) });
    if (createContextMethod == null)
      throw new InvalidOperationException("Could not find CreateContext method");

    using var context = (IDisposable)createContextMethod.Invoke(dbContextOperations, new object?[] { contextName });
    if (context == null)
      throw new InvalidOperationException("Could not create DbContext");

    var entityTypes = GetEntityTypes(GetModel(context));
    
    return entityTypes.Select(entityType => 
    {
      var entityName = GetEntityName(entityType);
      var tableName = GetTableName(entityType) ?? entityName;
      return (entityName, tableName);
    }).ToList();
  }

  private object? GetDbContextOperations(Type executorType)
  {
    var field = executorType.GetField("_contextOperations", BindingFlags.NonPublic | BindingFlags.Instance);
    if (field != null)
      return field.GetValue(_executor);
      
    var property = executorType.GetProperty("DbContextOperations", BindingFlags.NonPublic | BindingFlags.Instance);
    return property?.GetValue(_executor);
  }

  private object GetModel(IDisposable context)
  {
    var modelProperty = context.GetType().GetProperty("Model");
    if (modelProperty == null)
      throw new InvalidOperationException("Could not access Model property");
      
    var model = modelProperty.GetValue(context);
    if (model == null)
      throw new InvalidOperationException("Model is null");
      
    return model;
  }

  private IEnumerable<object> GetEntityTypes(object model)
  {
    var iModelType = FindIModelType();
    if (iModelType == null || !iModelType.IsAssignableFrom(model.GetType()))
      throw new InvalidOperationException("Could not cast to IModel");

    var getEntityTypesMethod = iModelType.GetMethod("GetEntityTypes", Type.EmptyTypes);
    if (getEntityTypesMethod == null)
      throw new InvalidOperationException("Could not find GetEntityTypes method");

    var entityTypesCollection = getEntityTypesMethod.Invoke(model, null);
    if (entityTypesCollection == null)
      throw new InvalidOperationException("EntityTypes collection is null");

    return (IEnumerable<object>)entityTypesCollection;
  }

  private Type? FindIModelType()
  {
    return AppDomain.CurrentDomain.GetAssemblies()
      .Where(a => a.FullName?.Contains("EntityFrameworkCore") == true)
      .Select(assembly => assembly.GetType("Microsoft.EntityFrameworkCore.Metadata.IModel"))
      .FirstOrDefault(type => type != null);
  }

  private string GetEntityName(object entityType)
  {
    var nameProperty = entityType.GetType().GetProperty("Name");
    var fullName = nameProperty?.GetValue(entityType)?.ToString();
    
    if (string.IsNullOrEmpty(fullName))
      throw new InvalidOperationException("Entity name is null or empty");

    // fullName is expected to be in the format "Namespace.EntityName"
    var dotIdx = fullName!.LastIndexOf('.');
    return dotIdx >= 0 ? fullName.Substring(dotIdx + 1) : fullName;
  }

  private string? GetTableName(object entityType)
  {
    try
    {
      var getAnnotationMethod = entityType.GetType().GetMethod("GetAnnotation", new[] { typeof(string) });
      if (getAnnotationMethod == null) return null;

      var tableAnnotation = getAnnotationMethod.Invoke(entityType, new object[] { "Relational:TableName" });
      var tableName = GetAnnotationValue(tableAnnotation);

      var schemaAnnotation = getAnnotationMethod.Invoke(entityType, new object[] { "Relational:Schema" });
      var schemaName = GetAnnotationValue(schemaAnnotation);

      return !string.IsNullOrEmpty(schemaName) && !string.IsNullOrEmpty(tableName) 
        ? $"{schemaName}.{tableName}" 
        : tableName;
    }
    catch
    {
      return null;
    }
  }

  private string? GetAnnotationValue(object? annotation)
  {
    if (annotation == null) return null;
    
    var valueProperty = annotation.GetType().GetProperty("Value");
    return valueProperty?.GetValue(annotation)?.ToString();
  }

  private TResult InvokeOperation<TResult>(string optype, IDictionary arguments)
  {
    var operationType = _commandsAssembly.GetType(optype, throwOnError: true, ignoreCase: true)!;
    var result = (dynamic)Activator.CreateInstance(_resultHandlerType)!;
    Activator.CreateInstance(operationType, _executor, result, arguments);
    if (result.ErrorType != null)
    {
      throw new WrappedException(result.ErrorType, result.ErrorMessage, result.ErrorStackTrace);
    }
    return (TResult)result.Result;
  }

  private Assembly? ResolveAssembly(object? sender, ResolveEventArgs args)
  {
    var assemblyName = new AssemblyName(args.Name);
    foreach (var extension in new[] { ".dll", ".exe" })
    {
      var path = Path.Combine(_appBasePath, assemblyName.Name + extension);
      if (File.Exists(path))
      {
        try
        {
          return Assembly.LoadFrom(path);
        }
        catch
        {
        }
      }
    }
    return null;
  }

  public string? GetEntitySourceLocation(string entityName, string? contextName)
  {
    try
    {
      var entityInfo = GetEntityInfoUsingMetadata(entityName, contextName);
      if (!entityInfo.HasValue)
      {
        return null;
      }

      return SearchUsingFileSystem(entityInfo.Value);
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error finding entity location: {ex.Message}");
      return null;
    }
  }
  
  private (Type? ClrType, string FullName, string? Namespace)? GetEntityInfoUsingMetadata(string entityName, string? contextName)
  {
    try
    {
      var executorType = _executor.GetType();
      var dbContextOperations = GetDbContextOperations(executorType);
      if (dbContextOperations == null) return null;
      
      var createContextMethod = dbContextOperations.GetType().GetMethod("CreateContext", new[] { typeof(string) });
      
      using var context = (IDisposable?)createContextMethod?.Invoke(dbContextOperations, new object?[] { contextName });
      if (context == null) return null;

      var model = GetModel(context);
      var entityTypes = GetEntityTypes(model);
      
      foreach (var entityType in entityTypes)
      {
        if (GetEntityName(entityType) == entityName)
        {
          var clrType = GetClrType(entityType);
          var fullName = clrType?.FullName ?? GetEntityFullName(entityType);
          var namespaceName = clrType?.Namespace ?? GetNamespaceFromFullName(fullName);
          return (clrType, fullName, namespaceName);
        }
      }
    }
    catch { }
    
    return null;
  }

  private Type? GetClrType(object entityType)
  {
    try
    {
      var clrTypeProperty = entityType.GetType().GetProperty("ClrType");
      return clrTypeProperty?.GetValue(entityType) as Type;
    }
    catch
    {
      return null;
    }
  }
  
  private string? SearchUsingFileSystem((Type? ClrType, string FullName, string? Namespace) entityInfo)
  {
    var className = entityInfo.FullName.Split('.').Last();
    var searchDirectories = new[] { _projectDir };

    if (entityInfo.Namespace != null)
    {
      var namespacePath = Path.Combine(_projectDir, entityInfo.Namespace.Replace('.', Path.DirectorySeparatorChar));
      if (Directory.Exists(namespacePath))
      {
        searchDirectories = new[] { namespacePath };
      }
    }

    foreach (var directory in searchDirectories)
    {
      var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains("bin") && !f.Contains("obj"));

      foreach (var file in files)
      {
        try
        {
          var sourceText = File.ReadAllText(file);
          var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: file);
          var root = syntaxTree.GetRoot();

          var classDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.ValueText == className);

          if (classDeclaration != null)
          {
            var span = syntaxTree.GetLineSpan(classDeclaration.Span);
            var path = Path.GetRelativePath(_projectDir, file).Replace(Path.DirectorySeparatorChar, '/');
            return $"{path}:{span.StartLinePosition.Line + 1}:{span.EndLinePosition.Line + 1}";
          }
        }
        catch
        {
          continue;
        }
      }
    }

    return null;
  }
  
  private string GetEntityFullName(object entityType)
  {
    var nameProperty = entityType.GetType().GetProperty("Name");
    return nameProperty?.GetValue(entityType)?.ToString() ?? string.Empty;
  }
  
  private string? GetNamespaceFromFullName(string fullName)
  {
    var lastDotIndex = fullName.LastIndexOf('.');
    return lastDotIndex >= 0 ? fullName.Substring(0, lastDotIndex) : null;
  }
}
