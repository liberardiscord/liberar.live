#include "pch.h"
#include "src/hook/tcp.hpp"
#include "src/hook/hooks.hpp"
#include "src/core/config.hpp"
#include "src/core/control.hpp"
#include "src/core/logger.hpp"
#include "src/core/utils.hpp"
#include "src/net/socks5.hpp"

namespace droute {

    static void TrackProxiedTcpSocket(SOCKET s) {
        std::unique_lock<std::shared_mutex> lock(g_stateMutex);

        // A connect can finish while the control worker is turning the route
        // off. In that race the socket must not escape the same transition that
        // interrupts the connections which completed a moment earlier.
        if (!g_enabled.load(std::memory_order_acquire)) {
            const int rc = shutdown(s, SD_BOTH);
            const int error = rc == 0 ? 0 : WSAGetLastError();
            if (rc == 0 || error == WSAENOTCONN || error == WSAESHUTDOWN) {
                LOG_INFO("proxy tcp %llu interrupted after disable race", (ULONG_PTR)s);
            } else {
                LOG_WARN("proxy tcp %llu interrupt after disable failed: wsa_error=%d",
                         (ULONG_PTR)s, error);
            }
            return;
        }

        g_proxyTcpSockets.insert(s);
        LOG_DEBUG("proxy tcp %llu tracked", (ULONG_PTR)s);
    }

    void InterruptProxiedTcpSockets() {
        size_t interrupted = 0;
        size_t failed = 0;

        // Mine_closesocket uses the same lock before the real close. Keeping it
        // here prevents a descriptor from being freed and reused between the
        // registry lookup and shutdown. We deliberately do not call
        // closesocket: ownership of the handle stays with Discord.
        std::unique_lock<std::shared_mutex> lock(g_stateMutex);
        for (SOCKET s : g_proxyTcpSockets) {
            const int rc = shutdown(s, SD_BOTH);
            const int error = rc == 0 ? 0 : WSAGetLastError();
            if (rc == 0 || error == WSAENOTCONN || error == WSAESHUTDOWN) {
                ++interrupted;
            } else {
                ++failed;
                LOG_WARN("proxy tcp %llu interrupt failed: wsa_error=%d",
                         (ULONG_PTR)s, error);
            }
        }

        g_proxyTcpSockets.clear();
        LOG_INFO("proxy tcp interrupted=%llu failed=%llu",
                 static_cast<unsigned long long>(interrupted),
                 static_cast<unsigned long long>(failed));
    }

    int ConnectToProxy(SOCKET s, const sockaddr_in& proxyAddr, uint64_t deadline) {
        int rc = Hooks::Real_connect(s, reinterpret_cast<const sockaddr*>(&proxyAddr), sizeof(proxyAddr));
        if (rc == 0)
            return 0;

        int err = WSAGetLastError();
        if (err != WSAEWOULDBLOCK && err != WSAEINPROGRESS)
            return SOCKET_ERROR;

        if (!WaitForConnect(s, RemainingTimeout(deadline))) {
            return SOCKET_ERROR;
        }

        int soErr = 0;
        int soLen = sizeof(soErr);
        if (getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&soErr), &soLen) != 0)
            return SOCKET_ERROR;
        if (soErr != 0) {
            WSASetLastError(soErr);
            return SOCKET_ERROR;
        }

        return 0;
    }

    int Socks5ProxyConnect(SOCKET s, const SessionCredential& credential,
                           const sockaddr_in& target, uint64_t deadline) {
        if (!Socks5Handshake(s, credential.user.c_str(), credential.password.c_str(), deadline)) {
            return SOCKET_ERROR;
        }
        if (!Socks5RequestConnect(s, target, deadline)) {
            return SOCKET_ERROR;
        }
        return 0;
    }

    int ConnectViaProxy(SOCKET s, const sockaddr_in* target, uint64_t deadline) {
        // Read the credential once per connection. Without one there is nothing
        // to fall back to, so the attempt fails instead of leaking direct.
        SessionCredential credential;
        if (!GetSessionCredential(credential)) {
            WSASetLastError(WSAEACCES);
            LOG_WARN("no session credential for connect (fail-closed)");
            return SOCKET_ERROR;
        }

        if (ConnectToProxy(s, credential.proxyAddr, deadline) != 0) {
            return SOCKET_ERROR;
        }
        if (Socks5ProxyConnect(s, credential, *target, deadline) != 0) {
            return SOCKET_ERROR;
        }
        return 0;
    }

    int WSAAPI Mine_connect(SOCKET s, const sockaddr* name, int namelen) {
        if (!g_enabled.load(std::memory_order_relaxed)) {
            return Hooks::Real_connect(s, name, namelen);
        }

        if (!name || namelen < static_cast<int>(sizeof(name->sa_family))) {
            return Hooks::Real_connect(s, name, namelen);
        }

        if (name->sa_family == AF_INET6) {
            if (namelen >= static_cast<int>(sizeof(sockaddr_in6)) &&
                IsLoopbackAddr(*reinterpret_cast<const sockaddr_in6*>(name))) {
                return Hooks::Real_connect(s, name, namelen);
            }
            WSASetLastError(WSAEACCES);
            LOG_WARN("blocked direct IPv6 connect (fail-closed)");
            return SOCKET_ERROR;
        }

        if (name->sa_family != AF_INET)
            return Hooks::Real_connect(s, name, namelen);

        if (namelen < static_cast<int>(sizeof(sockaddr_in))) {
            WSASetLastError(WSAEINVAL);
            return SOCKET_ERROR;
        }

        const sockaddr_in* addr = reinterpret_cast<const sockaddr_in*>(name);
        if (addr->sin_addr.s_addr == 0) {
            WSASetLastError(WSAEACCES);
            return SOCKET_ERROR;
        }

        if (IsLoopbackAddr(*addr) || IsSameAddr(*addr, GetProxyAddr())) {
            return Hooks::Real_connect(s, name, namelen);
        }
        if (IsUdpSocket(s)) {
            WSASetLastError(WSAEACCES);
            LOG_WARN("blocked connected UDP destination %s (fail-closed)", AddrToString(*addr).c_str());
            return SOCKET_ERROR;
        }

        bool wasNonBlocking = false;
        {
            std::shared_lock<std::shared_mutex> lock(g_stateMutex);
            wasNonBlocking = g_nonBlockingSockets.count(s) != 0;
        }

        const std::string target = AddrToString(*addr);
        const uint64_t startedAt = GetTickCount64();
        const uint64_t deadline = MakeDeadline(g_cfg.connectTimeout);

        bool changedToNonBlocking = false;
        if (!wasNonBlocking) {
            u_long mode = 1;
            if (Hooks::Real_ioctlsocket(s, FIONBIO, &mode) != 0)
                return SOCKET_ERROR;
            changedToNonBlocking = true;
        }

        int result = ConnectViaProxy(s, addr, deadline);
        int resultError = result == 0 ? 0 : WSAGetLastError();

        if (result == 0)
            TrackProxiedTcpSocket(s);

        if (changedToNonBlocking) {
            u_long mode = 0;
            if (Hooks::Real_ioctlsocket(s, FIONBIO, &mode) != 0 && result == 0) {
                result = SOCKET_ERROR;
                resultError = WSAGetLastError();
            }
        }

        if (result != 0) {
            WSASetLastError(resultError);
            LOG_WARN("connect -> %s failed: wsa_error=%d elapsed_ms=%llu",
                     target.c_str(), resultError, GetTickCount64() - startedAt);
            WSASetLastError(resultError);
            return SOCKET_ERROR;
        }

        const uint64_t elapsed = GetTickCount64() - startedAt;
        if (elapsed >= 1000) {
            LOG_WARN("connect -> %s slow mode=%s elapsed_ms=%llu", target.c_str(),
                     wasNonBlocking ? "nonblocking" : "blocking", elapsed);
        } else {
            LOG_DEBUG("connect -> %s via proxy mode=%s elapsed_ms=%llu", target.c_str(),
                      wasNonBlocking ? "nonblocking" : "blocking", elapsed);
        }

        if (wasNonBlocking) {
            WSASetLastError(WSAEWOULDBLOCK);
            return SOCKET_ERROR;
        }
        return 0;
    }

}
