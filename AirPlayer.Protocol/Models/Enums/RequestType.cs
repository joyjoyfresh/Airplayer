using System;

namespace AirPlayer.Protocol.Models.Enums
{
    public enum RequestType
    {
        GET,
        POST,
        SETUP,
        GET_PARAMETER,
        RECORD,
        SET_PARAMETER,
        OPTIONS,
        ANNOUNCE,
        FLUSH,
        TEARDOWN,
        PAUSE,
        SETPEERS
    }
}
