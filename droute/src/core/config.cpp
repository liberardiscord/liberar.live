#include "pch.h"
#include "src/core/config.hpp"
#include "src/core/utils.hpp"

namespace droute {

    Config g_cfg;

    bool Config::Load() {
        in_addr parsed{};
        if (inet_pton(AF_INET, host.c_str(), &parsed) != 1) {
            LOG_ERROR("invalid fallback proxy address");
            return false;
        }

        // No credential is logged because none exists at this point: the secret
        // arrives per activation and lives only in control.cpp.
        LOG_INFO("config source=embedded fallback=%s:%u auth=per-session connect_timeout_ms=%u reconnect_interval_ms=%u log_level=%s",
                 host.c_str(), port, connectTimeout, reconnectInterval, LevelToString(logLevel));
        return true;
    }

}
