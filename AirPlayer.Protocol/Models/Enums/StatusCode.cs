using System;

namespace AirPlayer.Protocol.Models.Enums
{
    public enum StatusCode
    {
        OK = 200,
        UNAUTHORIZED = 401,
        BADREQUEST = 400,
        NOTFOUND = 404,
        INTERNALSERVERERROR = 500
    }
}
