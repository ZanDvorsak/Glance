using System.IO.Pipes;
using System.Security.Principal;

namespace Glance.Core.Service;

/// <summary>The pipe name is already served by another program (a squatter), so this process does not serve it.</summary>
public sealed class PipeNameTakenException : IOException
{
    public PipeNameTakenException(string message) : base(message) { }
}

/// <summary>
/// Creates the server end of Glance's pipes (the service's display and sensor pipes, the tray's control pipe), so that a
/// pipe name another program took first is refused, never joined. Without FILE_FLAG_FIRST_PIPE_INSTANCE a server whose
/// account the squatter's DACL admits (SYSTEM always is, with .NET's defaults) silently adds its instances to the squatter's
/// pipe and serves them under the squatter's rules (review M1, L8).
/// </summary>
public static class ServicePipeFactory
{
    /// <param name="first">True when this server has no instance of the name yet: the name must then be free.</param>
    /// <exception cref="PipeNameTakenException">Another program serves the name, or the new instance is not owned by this account.</exception>
    public static NamedPipeServerStream Create(string name, PipeDirection direction, int maxInstances, PipeSecurity security, bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        NamedPipeServerStream server;
        try { server = NamedPipeServerStreamAcl.Create(name, direction, maxInstances, PipeTransmissionMode.Byte, options, 0, 0, security); }
        catch (Exception ex) when (first && ex is UnauthorizedAccessException or IOException)
        {
            throw new PipeNameTakenException($"The pipe name {name} is taken by another program ({ex.Message.TrimEnd('.')}); it is not served until that program closes it.");
        }

        // A later instance joins whatever pipe has the name. Ours, as long as one of our instances kept it alive; check anyway.
        var owner = PipeServerCheck.OwnerOf(server);
        using var me = WindowsIdentity.GetCurrent();
        if (owner is null || !(owner == me.User || owner == ServicePipeSecurity.Administrators))
        {
            server.Dispose();
            throw new PipeNameTakenException($"The pipe {name} belongs to {owner?.Value ?? "an unknown account"}, not to this program; it is not served.");
        }
        return server;
    }
}
