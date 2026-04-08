using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DonClaw.Shared;

public class NodeInvokeRequest
{
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public JsonElement Args { get; set; }
}

public class NodeInvokeResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public object? Payload { get; set; }
    public string? Error { get; set; }
}

public interface INodeCapability
{
    string Category { get; }
    IReadOnlyList<string> Commands { get; }
    bool CanHandle(string command);
    Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request);
}

public abstract class NodeCapabilityBase : INodeCapability
{
    public abstract string Category { get; }
    public abstract IReadOnlyList<string> Commands { get; }

    protected IOpenClawLogger Logger { get; }

    protected NodeCapabilityBase(IOpenClawLogger logger)
    {
        Logger = logger;
    }

    public virtual bool CanHandle(string command) => Commands.Contains(command);

    public abstract Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request);

    protected NodeInvokeResponse Success(object? payload = null)
        => new() { Ok = true, Payload = payload };

    protected NodeInvokeResponse Error(string message)
        => new() { Ok = false, Error = message };

    protected string? GetStringArg(JsonElement args, string name, string? defaultValue = null)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return defaultValue;
    }

    protected int GetIntArg(JsonElement args, string name, int defaultValue = 0)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            try { return prop.GetInt32(); }
            catch { return defaultValue; }
        }
        return defaultValue;
    }

    protected bool GetBoolArg(JsonElement args, string name, bool defaultValue = false)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (args.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }
}

public class NodeRegistration
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "windows";
    public string DisplayName { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public List<string> Commands { get; set; } = new();
    public Dictionary<string, bool> Permissions { get; set; } = new();
}
