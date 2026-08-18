#pragma once

// Public, non-operational defaults. Copy this file to proxy_config.local.hpp
// and replace the values only in your private working tree.
//
// There is deliberately no user or password here any more. Credentials are
// issued per activation by the broker and never compiled into the binary, so
// there is nothing in the build output for `strings` to recover.
//
// The endpoint below is only a fallback for private builds that talk to a fixed
// server. In the public build the broker supplies host and port with every
// credential, which is what allows the fleet to rotate without a new release.
#define DROUTE_PROXY_HOST "127.0.0.1"
#define DROUTE_PROXY_PORT 1080
