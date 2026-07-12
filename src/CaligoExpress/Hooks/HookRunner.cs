namespace CaligoExpress.Hooks;

using System.Text;
using CaligoExpress.Abstractions;
using CaligoExpress.Manifests;

/// <summary>A file a hook wants written, relative to the package's target root.</summary>
public sealed record HookOutput(string RelativePath, byte[] Content);

/// <summary>
/// Hooks are evaluated at prepare time and turned into ordinary staged file operations, so they run
/// inside the transaction and roll back with everything else — unlike the legacy system, where hooks
/// fired after the commit and could not be undone. Being pure file generators also makes them
/// idempotent: re-running an interrupted update reproduces the same outputs.
///
/// Supported kinds: <c>emit</c> (write a computed string, e.g. the version via "#version") and
/// <c>copy</c> (duplicate an existing package file to another path).
/// </summary>
public static class HookRunner
{
    public static IReadOnlyList<HookOutput> Run(PackageManifest manifest, Func<string, byte[]?> readFile)
    {
        var outputs = new List<HookOutput>();

        foreach (var hook in manifest.Hooks)
        {
            switch (hook.Kind)
            {
                case "emit":
                {
                    var content = hook.Query == "#version" ? manifest.Version : hook.Query ?? "";
                    outputs.Add(new HookOutput(RequireTarget(hook), Encoding.UTF8.GetBytes(content)));
                    break;
                }

                case "copy":
                {
                    var origin = hook.Origin
                        ?? throw new CaligoException($"copy hook '{hook.Id}' is missing its origin");
                    var bytes = readFile(origin)
                        ?? throw new CaligoException($"copy hook '{hook.Id}' origin '{origin}' was not found");
                    outputs.Add(new HookOutput(RequireTarget(hook), bytes));
                    break;
                }

                default:
                    throw new CaligoException($"unknown hook kind '{hook.Kind}' (hook '{hook.Id}')");
            }
        }

        return outputs;
    }

    private static string RequireTarget(HookDef hook)
        => hook.Target ?? throw new CaligoException($"hook '{hook.Id}' is missing its target");
}
