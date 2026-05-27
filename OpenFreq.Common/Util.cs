using System.Net;

namespace OpenFreq.Common;

public static class Util
{
    public static (string ipAddress, int port) ResolveAddress(string address, int defaultPort)
    {
        // Default port if no port is specified
        string ipAddress;
        int port = defaultPort;

        // Check if address contains a port (separated by a colon)
        string[] parts = address.Split(':');
        if (parts.Length > 2)
        {
            throw new ArgumentException("Invalid address format.");
        }

        // Check if the second part contains a valid port number
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], out port) || port < 1 || port > 65535)
            {
                throw new ArgumentException("Invalid port number.");
            }
        }

        // Try to parse the first part as an IP address
        if (IPAddress.TryParse(parts[0], out var ip))
        {
            ipAddress = ip.ToString();
        }
        else
        {
            // Otherwise, treat it as a hostname and try to resolve it
            try
            {
                ipAddress = Dns.GetHostEntry(parts[0]).AddressList[0].ToString();
            }
            catch (Exception)
            {
                throw new ArgumentException("Invalid hostname or could not resolve.");
            }
        }

        return (ipAddress, port);
    }

}
