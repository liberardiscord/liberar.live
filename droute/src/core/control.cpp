#include "pch.h"
#include "src/core/control.hpp"
#include "src/core/config.hpp"
#include "src/core/logger.hpp"
#include "src/hook/hooks.hpp"
#include "src/hook/tcp.hpp"
#include "src/hook/udp.hpp"

namespace droute {

    std::atomic<bool> g_enabled{ false };
    static std::atomic<bool> g_controlRunning{ false };

    static constexpr wchar_t kRegSubKey[]  = L"Software\\droute";
    static constexpr wchar_t kRegValueName[] = L"enabled";
    static constexpr wchar_t kRegDeadlineValueName[] = L"enabled_until";
    static constexpr wchar_t kRegSessionHost[] = L"session_host";
    static constexpr wchar_t kRegSessionPort[] = L"session_port";
    static constexpr wchar_t kRegSessionUser[] = L"session_user";
    static constexpr wchar_t kRegSessionPass[] = L"session_pass";
    static constexpr DWORD kPollIntervalMs = 300;

    // The endpoint is packed into one word so the per-packet paths can read it
    // without a lock: high 32 bits are the IPv4 address in network order, low 16
    // are the port in host order. Zero means "no activation loaded".
    static std::atomic<uint64_t> g_proxyEndpoint{ 0 };

    // The secret needs a real lock, but it is only read while establishing a
    // connection, never per packet.
    static std::shared_mutex g_credentialMutex;
    static std::string g_sessionUser;
    static std::string g_sessionPassword;

    static ULONGLONG UnixTimeNow() {
        FILETIME fileTime{};
        GetSystemTimeAsFileTime(&fileTime);
        ULARGE_INTEGER ticks{};
        ticks.LowPart = fileTime.dwLowDateTime;
        ticks.HighPart = fileTime.dwHighDateTime;
        return (ticks.QuadPart / 10000000ULL) - 11644473600ULL;
    }

    sockaddr_in GetProxyAddr() {
        sockaddr_in addr{};
        addr.sin_family = AF_INET;

        const uint64_t packed = g_proxyEndpoint.load(std::memory_order_acquire);
        if (packed == 0)
            return addr;

        const uint32_t netAddr = static_cast<uint32_t>(packed >> 32);
        const uint16_t port = static_cast<uint16_t>(packed & 0xFFFF);
        memcpy(&addr.sin_addr, &netAddr, sizeof(netAddr));
        addr.sin_port = htons(port);
        return addr;
    }

    bool GetSessionCredential(SessionCredential& out) {
        const uint64_t packed = g_proxyEndpoint.load(std::memory_order_acquire);
        if (packed == 0)
            return false;

        std::shared_lock<std::shared_mutex> lock(g_credentialMutex);
        if (g_sessionUser.empty() || g_sessionPassword.empty())
            return false;

        out.proxyAddr = GetProxyAddr();
        out.user = g_sessionUser;
        out.password = g_sessionPassword;
        return true;
    }

    // Reads a REG_SZ into UTF-8. Returns false when the value is missing, of the
    // wrong type, or implausibly long for what it carries.
    static bool ReadRegString(HKEY key, const wchar_t* name, std::string& out, DWORD maxChars) {
        DWORD type = 0;
        DWORD bytes = 0;
        if (RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes) != ERROR_SUCCESS)
            return false;
        if (type != REG_SZ || bytes == 0 || bytes > (maxChars + 1) * sizeof(wchar_t))
            return false;

        std::vector<wchar_t> buffer(bytes / sizeof(wchar_t) + 1, L'\0');
        DWORD size = bytes;
        if (RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<LPBYTE>(buffer.data()),
                             &size) != ERROR_SUCCESS)
            return false;
        buffer[bytes / sizeof(wchar_t)] = L'\0';

        const int needed = WideCharToMultiByte(CP_UTF8, 0, buffer.data(), -1,
                                               nullptr, 0, nullptr, nullptr);
        if (needed <= 1)
            return false;

        out.assign(static_cast<size_t>(needed) - 1, '\0');
        if (WideCharToMultiByte(CP_UTF8, 0, buffer.data(), -1, &out[0], needed,
                                nullptr, nullptr) == 0)
            return false;

        SecureZeroMemory(buffer.data(), buffer.size() * sizeof(wchar_t));
        return true;
    }

    static void ClearSessionCredential() {
        g_proxyEndpoint.store(0, std::memory_order_release);

        std::unique_lock<std::shared_mutex> lock(g_credentialMutex);
        if (!g_sessionUser.empty())
            SecureZeroMemory(&g_sessionUser[0], g_sessionUser.size());
        if (!g_sessionPassword.empty())
            SecureZeroMemory(&g_sessionPassword[0], g_sessionPassword.size());
        g_sessionUser.clear();
        g_sessionPassword.clear();
    }

    // Loads the credential the installer wrote for this activation. There is no
    // compiled-in fallback on purpose: without an issued credential the client
    // must stay on the direct connection rather than reach for a shared secret.
    static bool LoadSessionCredential() {
        HKEY key = nullptr;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, kRegSubKey, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS)
            return false;

        std::string host;
        std::string user;
        std::string password;
        DWORD port = 0;
        DWORD size = sizeof(port);
        DWORD type = 0;

        // The secret is mandatory. The endpoint is not: when the activation does
        // not carry one, the build's fallback applies, which is what keeps a
        // private build against a fixed server working.
        const bool haveSecret = ReadRegString(key, kRegSessionUser, user, 128) &&
                                ReadRegString(key, kRegSessionPass, password, 128);

        if (!ReadRegString(key, kRegSessionHost, host, 253))
            host = g_cfg.host;

        if (RegQueryValueExW(key, kRegSessionPort, nullptr, &type,
                             reinterpret_cast<LPBYTE>(&port), &size) != ERROR_SUCCESS ||
            type != REG_DWORD || port == 0 || port > 65535)
            port = g_cfg.port;

        RegCloseKey(key);

        if (!haveSecret) {
            LOG_WARN("no session credential available; staying on the direct connection");
            return false;
        }

        in_addr parsed{};
        if (inet_pton(AF_INET, host.c_str(), &parsed) != 1) {
            // A DNS name keeps node replacement independent from both the client
            // release and the broker configuration. Resolution happens while the
            // route is still direct; only the resulting IPv4 address is published
            // to the hot packet paths.
            addrinfo hints = {};
            hints.ai_family = AF_INET;
            hints.ai_socktype = SOCK_STREAM;
            hints.ai_protocol = IPPROTO_TCP;

            addrinfo* result = nullptr;
            const int error = getaddrinfo(host.c_str(), nullptr, &hints, &result);
            if (error != 0 || !result) {
                LOG_ERROR("session credential has an invalid or unresolved proxy host");
                return false;
            }
            parsed = reinterpret_cast<sockaddr_in*>(result->ai_addr)->sin_addr;
            freeaddrinfo(result);
        }

        uint32_t netAddr = 0;
        memcpy(&netAddr, &parsed, sizeof(netAddr));

        {
            std::unique_lock<std::shared_mutex> lock(g_credentialMutex);
            g_sessionUser = user;
            g_sessionPassword = password;
        }
        g_proxyEndpoint.store((static_cast<uint64_t>(netAddr) << 32) | port,
                              std::memory_order_release);

        SecureZeroMemory(&password[0], password.size());
        LOG_INFO("session credential loaded for %s:%u", host.c_str(), port);
        return true;
    }

    // Returns false when the key/value is absent, so callers keep the default.
    static bool ReadControlState(bool& enabled, ULONGLONG& deadline) {
        HKEY key = nullptr;
        LONG status = RegOpenKeyExW(HKEY_CURRENT_USER, kRegSubKey, 0, KEY_QUERY_VALUE, &key);
        if (status != ERROR_SUCCESS)
            return false;

        DWORD data = 1;
        DWORD size = sizeof(data);
        DWORD type = 0;
        status = RegQueryValueExW(key, kRegValueName, nullptr, nullptr,
                                  reinterpret_cast<LPBYTE>(&data), &size);
        if (status != ERROR_SUCCESS) {
            RegCloseKey(key);
            return false;
        }

        deadline = 0;
        size = sizeof(deadline);
        status = RegQueryValueExW(key, kRegDeadlineValueName, nullptr, &type,
                                  reinterpret_cast<LPBYTE>(&deadline), &size);
        RegCloseKey(key);
        if (status != ERROR_SUCCESS || type != REG_QWORD || size != sizeof(deadline))
            deadline = 0;

        enabled = data != 0;
        return true;
    }

    static void PersistDisabledState() {
        HKEY key = nullptr;
        DWORD disposition = 0;
        LONG status = RegCreateKeyExW(HKEY_CURRENT_USER, kRegSubKey, 0, nullptr, 0,
                                      KEY_SET_VALUE, nullptr, &key, &disposition);
        if (status != ERROR_SUCCESS)
            return;

        DWORD disabled = 0;
        RegSetValueExW(key, kRegValueName, 0, REG_DWORD,
                       reinterpret_cast<const BYTE*>(&disabled), sizeof(disabled));
        RegDeleteValueW(key, kRegDeadlineValueName);
        RegDeleteValueW(key, kRegSessionUser);
        RegDeleteValueW(key, kRegSessionPass);
        RegCloseKey(key);
    }

    static bool EnforceDeadline(bool desired, ULONGLONG deadline) {
        if (!desired)
            return false;
        if (deadline != 0 && deadline > UnixTimeNow())
            return true;

        PersistDisabledState();
        LOG_INFO("proxy activation expired; direct connection restored");
        return false;
    }

    static void ControlWorker() {
        bool current = g_enabled.load(std::memory_order_relaxed);

        while (g_controlRunning.load(std::memory_order_relaxed)) {
            bool desired = false;
            ULONGLONG deadline = 0;
            if (ReadControlState(desired, deadline)) {
                desired = EnforceDeadline(desired, deadline);
            }

            // The credential is re-read on every transition, because a new one is
            // issued for each activation and Config::Load runs only once.
            if (desired && !current && !LoadSessionCredential())
                desired = false;

            if (desired != current) {
                if (!desired) {
                    // Publish the direct mode first. A proxy connect which was
                    // already in flight will notice this after its handshake
                    // and interrupt itself instead of escaping the transition.
                    g_enabled.store(false, std::memory_order_release);
                    TearDownUdpAssociations();
                    InterruptProxiedTcpSockets();
                    ClearSessionCredential();
                }
                current = desired;
                if (desired)
                    g_enabled.store(true, std::memory_order_release);
                LOG_INFO("proxy toggled enabled=%d", current ? 1 : 0);
            }
            Sleep(kPollIntervalMs);
        }
    }

    void ControlInit() {
        bool initial = false;
        ULONGLONG deadline = 0;
        if (ReadControlState(initial, deadline)) {
            initial = EnforceDeadline(initial, deadline);
            if (initial && !LoadSessionCredential())
                initial = false;
            g_enabled.store(initial, std::memory_order_relaxed);
        }

        g_controlRunning = true;
        try {
            std::thread(ControlWorker).detach();
        } catch (...) {
            g_controlRunning = false;
            LOG_ERROR("failed to start control worker");
        }
    }

    void ControlShutdown() {
        g_controlRunning = false;
        ClearSessionCredential();
    }

}
