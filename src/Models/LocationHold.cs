using ESPresense.Extensions;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

/// <summary>
/// Keeps a device's reported floor and room until a challenger has earned the switch, so a fix that
/// wobbles across a wall doesn't flip the room sensor.
/// - Room: a fix outside the current room but within <see cref="ConfigFiltering.RoomMargin"/> of its wall
///   keeps the room. A different room (or none) must hold for <see cref="ConfigFiltering.RoomHold"/> seconds.
/// - Floor: a different floor must hold for <see cref="ConfigFiltering.FloorHold"/> seconds. Its room is taken
///   as it is at that moment. While a floor challenge is pending, the room stays put too.
/// A setting of 0 turns that part off.
/// </summary>
public sealed class LocationHold
{
    private bool _held;
    private Floor? _challengerFloor;
    private DateTime _floorSince;
    private bool _floorChallenge;
    private Room? _challengerRoom;
    private DateTime _roomSince;
    private bool _roomChallenge;

    /// <summary>Whether a floor and room have been taken since the last <see cref="Reset"/></summary>
    public bool IsHeld => _held;

    public Floor? Floor { get; private set; }
    public Room? Room { get; private set; }

    /// <summary>What's waiting to take over, if anything: a floor, or a room on the current floor</summary>
    public string? Challenger => _floorChallenge ? _challengerFloor?.Name ?? _challengerFloor?.Id ?? "no floor"
        : _roomChallenge ? _challengerRoom?.Name ?? _challengerRoom?.Id ?? "no room" : null;

    /// <summary>When <see cref="Challenger"/> started leading</summary>
    public DateTime? ChallengerSince => _floorChallenge ? _floorSince : _roomChallenge ? _roomSince : null;

    /// <summary>Forget everything: the next <see cref="Update"/> is taken as it is</summary>
    public void Reset()
    {
        _held = false;
        Floor = null;
        Room = null;
        _floorChallenge = _roomChallenge = false;
    }

    /// <param name="floor">The best scenario's floor this pass</param>
    /// <param name="room">The best scenario's room this pass</param>
    /// <param name="at">The position the room was worked out from</param>
    /// <returns>Whether the held floor or room changed</returns>
    public bool Update(Floor? floor, Room? room, Point3D at, DateTime now, ConfigFiltering settings)
    {
        if (!_held)
        {
            Take(floor, room);
            return true;
        }

        if (floor != Floor)
        {
            _roomChallenge = false;
            if (!_floorChallenge || _challengerFloor != floor)
            {
                _floorChallenge = true;
                _challengerFloor = floor;
                _floorSince = now;
            }

            if ((now - _floorSince).TotalSeconds < settings.FloorHold) return false;
            Take(floor, room);
            return true;
        }

        _floorChallenge = false;

        if (room == Room || (Room?.Polygon is { } polygon && settings.RoomMargin > 0 &&
                             SpatialUtils.DistanceToEdge(polygon, at.ToPoint2D()) <= settings.RoomMargin))
        {
            _roomChallenge = false;
            return false;
        }

        if (!_roomChallenge || _challengerRoom != room)
        {
            _roomChallenge = true;
            _challengerRoom = room;
            _roomSince = now;
        }

        if ((now - _roomSince).TotalSeconds < settings.RoomHold) return false;
        Take(floor, room);
        return true;
    }

    private void Take(Floor? floor, Room? room)
    {
        _held = true;
        Floor = floor;
        Room = room;
        _floorChallenge = _roomChallenge = false;
    }
}
