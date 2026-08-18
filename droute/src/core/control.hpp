#pragma once

#include "pch.h"

namespace droute {

    // Global runtime switch read by every hook. The registry deadline makes
    // every activation expire automatically after the installer-defined window.
    // false -> behave like a vanilla Discord client (new connections go direct).
    //
    // The local deadline is a convenience, not a control: a modified client can
    // ignore it. What actually bounds an activation is the credential's own
    // lifetime on the server, which expires whether or not this process agrees.
    extern std::atomic<bool> g_enabled;

    // Credential for the current activation. It is issued per activation by the
    // broker, so nothing here is compiled into the binary and nothing survives
    // the session.
    struct SessionCredential {
        sockaddr_in proxyAddr = {};
        std::string user;
        std::string password;
    };

    // Endpoint of the current activation. Backed by a single atomic word so the
    // per-packet paths can read it without taking a lock. Returns a zeroed
    // address when no activation is loaded.
    sockaddr_in GetProxyAddr();

    // Full credential, including the secret. Takes a lock, so call it once while
    // establishing a connection and never per packet. Returns false when no
    // activation is loaded.
    bool GetSessionCredential(SessionCredential& out);

    // Starts the registry poller thread. Safe to call once after hooks are installed.
    void ControlInit();

    // Stops the poller thread (DLL_PROCESS_DETACH).
    void ControlShutdown();

}
