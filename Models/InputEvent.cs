namespace Daylane.Models;

internal readonly struct InputEvent
{
    public InputEvent(DateTime timestampUtc, string eventType, int x, int y)
    {
        TimestampUtc = timestampUtc;
        EventType = eventType;
        X = x;
        Y = y;
    }

    public DateTime TimestampUtc { get; }
    public string EventType { get; }
    public int X { get; }
    public int Y { get; }
}
