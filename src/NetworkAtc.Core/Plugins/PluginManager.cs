using System.Reflection;
using System.Runtime.Loader;
using NetworkAtc.Core.Session;
using NetworkAtc.Core.Tags;
using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Plugins;

public sealed record LoadedPlugin(IAtcPlugin Plugin, string File);

public sealed record PluginLoadError(string File, string Reason);

/// <summary>
/// Loads plugins: every *.dll in the plugins folder (and its sub-folders) that contains a public
/// <see cref="IAtcPlugin"/> implementation. Each DLL gets its own load context, sharing only the
/// plugin API assembly with the application.
/// </summary>
public sealed class PluginManager(AtcSession session, TagFields fields, PluginRegistry registry, Func<IAircraft?> selected, string dataRoot)
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<PluginLoadError> _errors = [];

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public IReadOnlyList<PluginLoadError> Errors => _errors;
    public PluginRegistry Registry => registry;

    public void LoadFrom(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals("NetworkAtc.PluginApi.dll", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var context = new PluginLoadContext(file);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                foreach (var type in types.Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IAtcPlugin).IsAssignableFrom(t)))
                    Add((IAtcPlugin)Activator.CreateInstance(type)!, file);
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or MissingMethodException or TargetInvocationException or TypeLoadException)
            {
                // Native DLLs (e.g. EuroScope plugins) and broken assemblies end up here.
                _errors.Add(new PluginLoadError(file, e is BadImageFormatException
                    ? "not a .NET assembly (EuroScope plugins are not supported here, see README)"
                    : e.Message));
            }
        }
    }

    /// <summary>Register an already created plugin (used for built-in plugins and tests).</summary>
    public void Add(IAtcPlugin plugin, string file = "")
    {
        string safeName = string.Concat(plugin.Name.Split(Path.GetInvalidFileNameChars()));
        string dataDir = Path.Combine(dataRoot, safeName);
        Directory.CreateDirectory(dataDir);
        try
        {
            plugin.Initialize(new PluginHost(plugin.Name, session, fields, registry, selected, dataDir));
            _plugins.Add(new LoadedPlugin(plugin, file));
        }
        catch (Exception e)
        {
            _errors.Add(new PluginLoadError(file.Length > 0 ? file : plugin.Name, "initialization error: " + e.Message));
        }
    }

    public void ShutdownAll()
    {
        foreach (var p in _plugins)
        {
            try { p.Plugin.Shutdown(); }
            catch (Exception) { /* closing anyway */ }
        }
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // The API must be the application's copy, otherwise IAtcPlugin types would not match.
            if (name.Name == typeof(IAtcPlugin).Assembly.GetName().Name) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
