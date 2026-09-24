// The world as the plugins see it: aircraft, controllers, sector elements, radio channels and lists.
// Interface handles (CFlightPlan, CRadarTarget, CController…) point at these records. Records are never
// freed while the host runs (an aircraft that leaves is only marked gone), so a handle a plugin keeps
// can never point at freed memory.
#pragma once

#include <array>
#include <cstdint>
#include <deque>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "Wire.h"

namespace natc
{
    // Strings are kept in the ANSI code page the plugins use; the wire carries UTF-8.
    std::string ToAnsi(const std::string& utf8);
    std::string ToUtf8(const char* ansi);

    struct RadarPosition
    {
        bool Valid = false;
        int ReceivedTime = 0;
        double Latitude = 0, Longitude = 0;
        std::string Squawk = "2000";
        bool ModeC = true, Ident = false;
        int PressureAltitude = 0, FlightLevel = 0, GroundSpeed = 0, Heading = 0, Pitch = 0, Bank = 0, RadarFlags = 7;
    };

    struct RoutePoint
    {
        std::string Name, Airway;
        double Latitude = 0, Longitude = 0;
        int AirwayClass = 0, Minutes = -1, ProfileAltitude = 0;
    };

    struct Prediction
    {
        double Latitude = 0, Longitude = 0;
        int Altitude = 0;
        std::string ControllerId;
    };

    struct Aircraft
    {
        std::string Callsign, PilotName, SystemId;
        bool Gone = false, HasRadar = false, HasFlightPlan = false, Correlated = true;

        // Radar: the newest position first; the slots never move, CRadarTargetPositionData points at them.
        static constexpr int HistorySize = 16;
        std::array<RadarPosition, HistorySize> History{};
        int Newest = 0;  // index of the newest position in History
        int VerticalSpeed = 0;
        double TrackHeading = 0;
        RadarPosition FpTrack;  // "flight plan track" position when there is no radar

        // Flight plan
        bool FpReceived = false, Amended = false;
        std::string PlanType = "I", AircraftInfo, AircraftFpType, Manufacturer;
        char Wtc = 'M', AircraftType = 'L', EngineType = 'J', Capabilities = '?', CommunicationType = 'v';
        int Engines = 2, TrueAirspeed = 0, FinalAltitude = 0;
        bool Rvsm = true;
        std::string Origin, Destination, Alternate, Remarks, Route, Sid, Star, DepartureRwy, ArrivalRwy;
        std::string EstimatedDeparture, ActualDeparture, EnrouteHours, EnrouteMinutes, FuelHours, FuelMinutes;

        // Controller assigned data
        std::string AssignedSquawk, ScratchPad, DirectTo;
        int AssignedFinalAltitude = 0, ClearedAltitude = 0, AssignedSpeed = 0, AssignedMach = 0, AssignedRate = 0, AssignedHeading = 0;
        char AssignedCommunicationType = ' ';
        std::array<std::string, 9> Annotations{};

        // States
        int State = 0, FpState = 0;
        bool Simulated = false, TrackingIsMe = false, Ram = false, Clam = false, Clearance = false, TextCommunication = false;
        std::string TrackingCallsign, TrackingId, HandoffCallsign, HandoffId, NextCopx, NextFirCopx, GroundState;
        double DistanceToDestination = 0, DistanceFromOrigin = 0;
        int SectorEntryMinutes = -1, SectorExitMinutes = -1;
        std::string CoordinatedNextController;
        int CoordinatedNextControllerState = 1;
        int EntryPointState = 1, EntryAltitudeState = 1, EntryAltitude = 0, ExitPointState = 1, ExitAltitudeState = 1, ExitAltitude = 0;
        std::string EntryPoint, ExitPoint;

        std::vector<RoutePoint> Route_;
        int RouteCalculatedIndex = -1, RouteAssignedIndex = -1;
        std::vector<Prediction> Predictions;

        const RadarPosition& Current() const { return History[Newest]; }
    };

    struct Controller
    {
        std::string Callsign, PositionId, FullName, SectorFile;
        bool Gone = false, Identified = false, IsController = true, Breaking = false, OngoingAble = true;
        double Frequency = 199.998, Latitude = 0, Longitude = 0;
        int Rating = 0, Facility = 0, Range = 0;
    };

    struct SectorElement
    {
        int Type = 0;
        std::string Name, Airport;
        double Frequency = 0;
        std::vector<std::pair<double, double>> Positions;
        std::vector<std::string> Components;
        std::array<std::string, 2> RunwayNames{};
        std::array<int, 2> RunwayHeadings{};
        std::array<bool, 2> Departure{}, Arrival{};
    };

    struct Channel
    {
        std::string Name;
        double Frequency = 0;
        bool Primary = false, Atis = false, TextReceive = false, TextTransmit = false, VoiceReceive = false, VoiceTransmit = false,
             VoiceConnected = false;
    };

    struct FpListColumn
    {
        std::string Title, ItemPlugin, LeftPlugin, RightPlugin;
        int Width = 0, ItemCode = 0, LeftFunction = 0, RightFunction = 0;
        bool Centered = false;
    };

    struct FpList
    {
        int Id = 0, PluginId = 0;
        std::string Name;
        bool Visible = false, Changed = true;
        std::vector<FpListColumn> Columns;
        std::vector<std::string> Callsigns;
    };

    struct ScreenObject
    {
        int ScreenIndex = 0, ObjectType = 0;
        std::string ObjectId, Message;
        Rect Area;
        bool Moveable = false;
    };
}
