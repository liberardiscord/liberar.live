#pragma once

#include "src/hook/hooks.hpp"

namespace droute {

    bool TryUdpAssociate(UdpAssociation& out);
    void MarkUdpAssociationPending(SOCKET s, SOCKET expectedControlSocket = INVALID_SOCKET);

    // Closes only the SOCKS5 control channels and clears proxy tracking. The
    // application UDP sockets remain open, so Discord can keep using the same
    // media sockets over the direct connection after the route is disabled.
    void TearDownUdpAssociations();

}
