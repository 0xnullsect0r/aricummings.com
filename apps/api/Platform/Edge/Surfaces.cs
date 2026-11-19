using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Ari.Platform.Edge;

public enum Surface { Combined, Public, Admin, Internal }

public static class Surfaces
{
    private const string ItemKey = "Ari.Surface";

    /// <summary>Stamped on the connection as it opens. Read it here, never from a header.</summary>
    public static Surface Get(this HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ItemKey, out var value) == true
        && value is Surface surface
            ? surface
            : Surface.Internal; // least privilege when unmarked

    public static void Stamp(this ConnectionContext connection, Surface surface) =>
        connection.Items[ItemKey] = surface;

    /// <summary>Which surface does this local port belong to? Decided from the socket only.</summary>
    public static Surface ForPort(IPEndPoint local, Env env) =>
        local.Port == env.PublicPort ? (env.SeparateAdmin ? Surface.Public : Surface.Combined)
        : env.AdminPort is int admin && local.Port == admin ? Surface.Admin
        : local.Port == env.InternalPort ? Surface.Internal
        : Surface.Internal; // any other local port: refuse everything but internal paths
}
