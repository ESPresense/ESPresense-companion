using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ESPresense.Utils;

public static class IpUtils
{
    public static IEnumerable<IPAddress> GetLocalIpAddresses()
    {
        return from network in NetworkInterface.GetAllNetworkInterfaces()
            where network.OperationalStatus == OperationalStatus.Up
            select network.GetIPProperties()
            into properties
            where properties.GatewayAddresses.Count != 0
            from a in properties.UnicastAddresses.Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            select a.Address;
    }

    public static string GetLocalIpAddress()
    {
      var ipAddress = GetLocalIpAddresses()
            .OrderBy(ip => $"{ip}")
            .FirstOrDefault();
        return $"{ipAddress}";
    }
}