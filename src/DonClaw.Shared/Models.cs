namespace DonClaw.Shared;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public enum PairingStatus
{
    Unknown,
    Pending,    // Connected but awaiting approval
    Paired,     // Approved with device token
    Rejected    // Pairing was rejected
}

public class PairingStatusEventArgs : EventArgs
{
    public PairingStatus Status { get; }
    public string DeviceId { get; }
    public string? Message { get; }

    public PairingStatusEventArgs(PairingStatus status, string deviceId, string? message = null)
    {
        Status = status;
        DeviceId = deviceId;
        Message = message;
    }
}
