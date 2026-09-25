// Network-ATC plugin host (32-bit): runs EuroScope plugins for Network-ATC.
// Usage: NetworkAtc.EsHost.exe <pipe name>. Network-ATC starts it and talks to it over the pipe.
#include <windows.h>

extern "C" __declspec(dllimport) int NatcEsHostRun(const char* pipeName);

int WINAPI WinMain(HINSTANCE, HINSTANCE, LPSTR commandLine, int)
{
    // Plugins look for their files next to themselves, never in the host's working folder.
    SetDllDirectoryA("");
    return NatcEsHostRun(commandLine ? commandLine : "");
}
