#pragma once

#include "pch.h"
#include "logger.hpp"

// Private builds may provide this ignored file. Public builds intentionally use
// the harmless localhost placeholders from proxy_config.example.hpp.
#if __has_include("proxy_config.local.hpp")
#include "proxy_config.local.hpp"
#else
#include "proxy_config.example.hpp"
#endif

namespace droute {

    struct Config {
        // Fallback endpoint for private builds. The public build receives host
        // and port from the broker alongside each credential, so these are only
        // consulted when the activation did not carry an endpoint.
        std::string host = DROUTE_PROXY_HOST;
        uint16_t    port = DROUTE_PROXY_PORT;

        uint32_t connectTimeout = 5000;
        uint32_t reconnectInterval = 3000;
        LogLevel logLevel = LogLevel::Info;

        bool Load();
    };

    extern Config g_cfg;

}
