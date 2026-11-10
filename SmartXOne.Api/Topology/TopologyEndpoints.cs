using SmartXOne.Api.Infrastructure;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Topology;

/// <summary>Part 3 endpoints: mesh registration, minimum spanning tree routing, BFS reachability and DFS components.</summary>
public static class TopologyEndpoints
{
    public static void MapTopologyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/topology");
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TopologyEndpoints));

        group.MapPost("/nodes", (RegisterMeshNodeRequest request, TopologyService topology) =>
        {
            try
            {
                return Results.Ok(topology.AddOrReplaceNode(request));
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Register mesh node", request.Mac);
            }
            catch (InvalidOperationException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status409Conflict, "Register mesh node", request.Mac);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Register mesh node", request.Mac);
            }
        });

        group.MapPost("/links", (RegisterMeshLinkRequest request, TopologyService topology) =>
        {
            var subject = $"{request.A}-{request.B}";
            try
            {
                return Results.Ok(topology.AddOrUpdateLink(request));
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Register mesh link", subject);
            }
            catch (KeyNotFoundException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status404NotFound, "Register mesh link", subject);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Register mesh link", subject);
            }
        });

        group.MapPut("/nodes/{mac}/state", (string mac, SetMeshNodeStateRequest request, TopologyService topology) =>
        {
            if (request.Online is null && request.Battery is null)
            {
                return ApiProblem.BadRequest("Send 'online', 'battery' or both.");
            }

            try
            {
                return Results.Ok(topology.SetNodeState(mac, request));
            }
            catch (KeyNotFoundException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status404NotFound, "Change node state", mac);
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Change node state", mac);
            }
            catch (InvalidOperationException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status409Conflict, "Change node state", mac);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Change node state", mac);
            }
        });

        group.MapGet("/", (TopologyService topology) =>
        {
            try
            {
                return Results.Ok(topology.GetTopology());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Read topology", subject: null);
            }
        });

        group.MapGet("/mst", (TopologyService topology) =>
        {
            try
            {
                return Results.Ok(topology.GetSpanningTree());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Read spanning tree", subject: null);
            }
        });

        group.MapGet("/route/{mac}", (string mac, TopologyService topology) =>
        {
            try
            {
                return Results.Ok(topology.GetRoute(mac));
            }
            catch (KeyNotFoundException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status404NotFound, "Find route", mac);
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Find route", mac);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Find route", mac);
            }
        });

        // ?from= a node MAC or a subnet name; omitted means the gateway.
        group.MapGet("/reachable", (string? from, TopologyService topology) =>
        {
            try
            {
                return Results.Ok(topology.GetReachability(from));
            }
            catch (KeyNotFoundException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status404NotFound, "Reachability sweep", from);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Reachability sweep", from);
            }
        });

        group.MapGet("/components", (TopologyService topology) =>
        {
            try
            {
                return Results.Ok(topology.GetComponents());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Connected components", subject: null);
            }
        });
    }
}
