using System.Net;
using System.Net.Sockets;
using GamePingBooster.Core.Profiles;

namespace GamePingBooster.PathCheck;

internal static partial class Program
{
    /// <summary>
    /// The last gate a typed game server passes through: <see cref="LobbyRoutes.ToHostRoutes"/>, which the service
    /// runs over the addresses it resolved out of a player's settings.
    ///
    /// This checks the gate and not the resolution, because resolution needs a live tunnel, a profile on disk and a
    /// name server, none of which belong in a console program. What is worth pinning without one is the promise
    /// that matters most about a typed list: an address the player got wrong, or that happens to be their own
    /// network, their relay or a measurement landmark, is refused and says why - and never becomes a route that
    /// outlives the game.
    ///
    /// Deliberately the same entry point the lobby addresses use, because it has to be. A second filter written for
    /// server addresses would be a second set of rules to keep in step with this one.
    /// </summary>
    private static void ServerAddressChecks()
    {
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            var routes = LobbyRoutes.ToHostRoutes(["1.2.3.4"], [], [], rejected);
            Check("A public address becomes a /32 route", routes.SequenceEqual(["1.2.3.4/32"]) && rejected.Count == 0,
                string.Join(", ", routes));
        }
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            var routes = LobbyRoutes.ToHostRoutes([" 1.2.3.4 "], [], [], rejected);
            Check("Surrounding spaces are trimmed, not refused", routes.SequenceEqual(["1.2.3.4/32"]) && rejected.Count == 0,
                string.Join(", ", routes));
        }
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            var routes = LobbyRoutes.ToHostRoutes(["1.2.3.4/32"], [], [], rejected);
            Check("An explicit /32 is the same as writing none", routes.SequenceEqual(["1.2.3.4/32"]) && rejected.Count == 0,
                string.Join(", ", routes));
        }

        // The first address of each range LobbyRoutes refuses, restated here rather than asked of it, so a range
        // quietly dropped from the engine fails here instead of quietly routing a player's own printer.
        (string Address, string Range)[] specialUse =
        [
            ("0.1.2.3", "0.0.0.0/8"),
            ("10.1.2.3", "10.0.0.0/8"),
            ("100.64.0.1", "100.64.0.0/10"),
            ("127.0.0.1", "127.0.0.0/8"),
            ("169.254.1.2", "169.254.0.0/16"),
            ("172.16.0.1", "172.16.0.0/12"),
            ("172.31.255.254", "172.16.0.0/12"),
            ("192.168.1.2", "192.168.0.0/16"),
            ("224.0.0.1", "224.0.0.0/4"),
            ("240.0.0.1", "240.0.0.0/4"),
            ("255.255.255.255", "240.0.0.0/4"),
        ];
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            LobbyRoutes.ToHostRoutes(specialUse.Select(s => s.Address), [], [], rejected);
            var through = specialUse.Where(s => rejected.All(r => r.Entry != s.Address)).ToList();
            Check("Every special-use range is refused", through.Count == 0,
                through.Count == 0 ? "" : string.Join(", ", through.Select(s => $"{s.Address} ({s.Range})")));
        }

        {
            var rejected = new List<LobbyRoutes.Rejection>();
            LobbyRoutes.ToHostRoutes(["1.2.3.0/24", "1.2.3.4/31", "1.2.3.4/8"], [], [], rejected);
            Check("A range is refused however public the address in it is", rejected.Count == 3,
                string.Join(" | ", rejected.Select(r => $"{r.Entry}: {r.Reason}")));
        }
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            LobbyRoutes.ToHostRoutes(["203.0.113.7"], ["203.0.113.7:51820"], [], rejected);
            Check("A relay's own address is refused - it is pinned to the physical adapter",
                rejected.Count == 1 && rejected[0].Reason.Contains("relay", StringComparison.OrdinalIgnoreCase),
                string.Join(" | ", rejected.Select(r => r.Reason)));
        }
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            LobbyRoutes.ToHostRoutes(["198.51.100.9"], [], ["198.51.100.9"], rejected);
            Check("A landmark is refused - routing one makes the game measure it through the relay",
                rejected.Count == 1 && rejected[0].Reason.Contains("landmark", StringComparison.OrdinalIgnoreCase),
                string.Join(" | ", rejected.Select(r => r.Reason)));
        }
        {
            // The shapes IPAddress.TryParse would accept and quietly mean something else. A player who types "10.1"
            // has made a mistake; a route to 10.0.0.1 would be a different mistake, and a much worse one.
            var rejected = new List<LobbyRoutes.Rejection>();
            LobbyRoutes.ToHostRoutes(["10.1", "1", "1.2.3", "1.2.3.4.5", "0x1.2.3.4", "1.2.3.4:25565"], [], [], rejected);
            Check("A short, odd or ported form is refused, not reinterpreted", rejected.Count == 6,
                string.Join(" | ", rejected.Select(r => r.Entry)));
        }
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            var routes = LobbyRoutes.ToHostRoutes(["2001:db8::1", "::1", "::ffff:1.2.3.4"], [], [], rejected);
            Check("IPv6 is refused - these routes are IPv4 only", routes.Count == 0 && rejected.Count == 3,
                string.Join(" | ", routes));
        }
        {
            // A name is what the box actually holds, so this is the contract that resolution happens BEFORE the
            // route table: a name reaching this gate unresolved is refused, never routed.
            var rejected = new List<LobbyRoutes.Rejection>();
            var routes = LobbyRoutes.ToHostRoutes(["mc.example.com"], [], [], rejected);
            Check("A name is refused here, so an unresolved one can never become a route",
                routes.Count == 0 && rejected.Count == 1, string.Join(", ", routes));
        }
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            var routes = LobbyRoutes.ToHostRoutes(["1.2.3.4", "5.6.7.8", "1.2.3.4", " 1.2.3.4 ", "5.6.7.8"], [], [], rejected);
            Check("Duplicates collapse and the order the player typed is kept",
                routes.SequenceEqual(["1.2.3.4/32", "5.6.7.8/32"]), string.Join(", ", routes));
        }
        {
            // A list with nothing usable in it is not a failure: it is a player who typed a name that has not
            // propagated yet, and it must leave the tunnel up and the game on its normal path.
            var rejected = new List<LobbyRoutes.Rejection>();
            var routes = LobbyRoutes.ToHostRoutes(["10.0.0.1", "nope.invalid", "1.2.3.0/24"], [], [], rejected);
            Check("A list with nothing usable in it installs nothing and does not throw",
                routes.Count == 0 && rejected.Count == 3, $"{routes.Count} routes, {rejected.Count} refusals");
        }
        {
            var rejected = new List<LobbyRoutes.Rejection>();
            LobbyRoutes.ToHostRoutes(["10.0.0.1", "1.2.3.0/24", "nope.invalid", "203.0.113.7"], ["203.0.113.7:1"], [], rejected);
            Check("Every refusal says why, because a silent drop is unexplainable later",
                rejected.All(r => !string.IsNullOrWhiteSpace(r.Reason)),
                string.Join(" | ", rejected.Where(r => string.IsNullOrWhiteSpace(r.Reason)).Select(r => r.Entry)));
        }

        ServerAddressPropertyChecks();
    }

    /// <summary>
    /// The claim that has to hold for EVERY typed list, not just the shapes above: whatever comes out of this gate is
    /// a single public address, and is not a relay and not a landmark. The engine's own range test is not consulted
    /// here - the ranges are restated in <see cref="Forbidden"/> so a range deleted from the engine arrives here as a
    /// failure rather than as a player's traffic going somewhere it should not.
    /// </summary>
    private static void ServerAddressPropertyChecks()
    {
        var rng = new Random(Seed + 40);
        string[] relays = ["203.0.113.7", "203.0.113.8", "198.51.100.200"];
        string[] landmarks = ["198.51.100.9", "198.51.100.10"];
        string? failure = null;

        for (var round = 0; round < 500 && failure is null; round++)
        {
            var entries = new List<string>();
            for (var i = 0; i < 40; i++)
            {
                var a = rng.Next(1, 255);
                var b = rng.Next(1, 255);
                var c = rng.Next(1, 255);
                var d = rng.Next(1, 255);
                switch (rng.Next(9))
                {
                    case 0:
                        entries.Add($"{a}.{b}.{c}.{d}");
                        break;
                    case 1:
                        entries.Add($"10.{a}.{b}.{c}");
                        break;
                    case 2:
                        entries.Add($"192.168.{a}.{b}");
                        break;
                    case 3:
                        entries.Add($"100.{64 + rng.Next(0, 64)}.{a}.{b}");
                        break;
                    case 4:
                        entries.Add($"{a}.{b}.{c}.0/24");
                        break;
                    case 5:
                        entries.Add($"mc{rng.Next(100)}.example.com");
                        break;
                    case 6:
                        entries.Add(rng.Next(2) == 0 ? relays[rng.Next(relays.Length)] : landmarks[rng.Next(landmarks.Length)]);
                        break;
                    case 7:
                        entries.Add(rng.Next(5) switch
                        {
                            0 => "  ",
                            1 => "10.1",
                            2 => "::1",
                            3 => "2001:db8::1",
                            _ => "1.2.3.4:25565",
                        });
                        break;
                    default:
                        entries.Add($"127.{a}.{b}.{c}");
                        break;
                }
            }

            var rejected = new List<LobbyRoutes.Rejection>();
            List<string> routes;
            try
            {
                routes = LobbyRoutes.ToHostRoutes(entries, relays, landmarks, rejected);
            }
            catch (Exception ex)
            {
                failure = $"seed {Seed}, round {round}: threw on a list of {entries.Count}: {ex.Message}";
                break;
            }

            foreach (var route in routes)
            {
                var at = $"seed {Seed}, round {round}: {route}";
                if (!route.EndsWith("/32", StringComparison.Ordinal))
                {
                    failure = $"{at} is not a single address";
                    break;
                }
                if (!IPAddress.TryParse(route[..^3], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    failure = $"{at} does not parse as IPv4";
                    break;
                }
                if (Forbidden(ip)) { failure = $"{at} is special-use"; break; }
                var text = ip.ToString();
                if (relays.Contains(text)) { failure = $"{at} is a relay"; break; }
                if (landmarks.Contains(text)) { failure = $"{at} is a landmark"; break; }
            }
        }

        Check("20 000 random entries: nothing unsafe survives", failure is null, failure ?? "");
    }

    /// <summary>
    /// The engine's list of ranges it refuses, said a second time and in a different shape. Longhand on purpose: it
    /// is the check on the check, and it is only worth something while it shares no code with the thing it audits.
    /// </summary>
    private static bool Forbidden(IPAddress ip)
    {
        static bool In(IPAddress candidate, string network, int bits)
        {
            var a = candidate.GetAddressBytes();
            var b = IPAddress.Parse(network).GetAddressBytes();
            var whole = bits / 8;
            for (var i = 0; i < whole; i++) if (a[i] != b[i]) return false;
            if (bits % 8 == 0) return true;
            var mask = (byte)(0xFF << (8 - bits % 8));
            return (a[whole] & mask) == (b[whole] & mask);
        }

        return In(ip, "0.0.0.0", 8) || In(ip, "10.0.0.0", 8) || In(ip, "100.64.0.0", 10) || In(ip, "127.0.0.0", 8) ||
               In(ip, "169.254.0.0", 16) || In(ip, "172.16.0.0", 12) || In(ip, "192.168.0.0", 16) ||
               In(ip, "224.0.0.0", 4) || In(ip, "240.0.0.0", 4);
    }
}
