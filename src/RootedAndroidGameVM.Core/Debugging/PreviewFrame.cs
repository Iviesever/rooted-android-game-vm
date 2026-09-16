namespace RootedAndroidGameVM.Core.Debugging;

public sealed record PreviewMetadata(string Session, int Width, int Height, int NativeWidth, int NativeHeight,
    int Rotation, int ImageRotation, string? Foreground, bool Awake, bool Locked, DateTimeOffset CapturedAt,
    int PayloadBytes, string Encoding = "png", bool BinaryPayload = true);
public sealed record PreviewFrame(PreviewMetadata Metadata, ReadOnlyMemory<byte> Payload);
