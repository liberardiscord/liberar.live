#pragma once

#include "src/hook/hooks.hpp"
#include "src/core/control.hpp"

namespace droute {

    // The credential is passed explicitly rather than read from a global: it is
    // issued per activation, so there is no build-time value these could fall
    // back to.
    int ConnectToProxy(SOCKET s, const sockaddr_in& proxyAddr, uint64_t deadline);
    int Socks5ProxyConnect(SOCKET s, const SessionCredential& credential,
                           const sockaddr_in& target, uint64_t deadline);
    int ConnectViaProxy(SOCKET s, const sockaddr_in* target, uint64_t deadline);

    // Stops I/O on TCP connections established through the proxy without
    // freeing Discord's socket handles. Discord remains responsible for the
    // matching closesocket calls and can reconnect those services directly.
    void InterruptProxiedTcpSockets();

}
