
using Chickensoft.Log;
using Chickensoft.Log.Godot;

namespace NovMai;

/// <summary>
/// Logger factory for the game.
/// </summary>
/// <remarks>
/// Uses <see cref="GDWriter"/> so messages reach the Godot console in every
/// build type. A <c>TraceWriter</c> only shows up when Godot installs its
/// <c>Trace</c> listener (editor/debug runs), so exported release builds would
/// silently drop all log output.
/// </remarks>
public static class Logging
{
    /// <summary>Creates a log named after <typeparamref name="T"/>.</summary>
    public static ILog Get<T>() => Get(typeof(T).Name);

    /// <summary>
    /// Creates a log with an explicit name. Useful for static classes, which
    /// cannot be used as type arguments.
    /// </summary>
    public static ILog Get(string name) => new Log(name, new GDWriter());
}
