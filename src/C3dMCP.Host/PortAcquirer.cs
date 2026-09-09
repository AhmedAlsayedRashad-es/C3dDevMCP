using System;
using System.Net;
using C3dMCP.Engine;

namespace C3dMCP.Host
{
    /// <summary>Tries the PortPlan candidates in order on http://localhost:{port}/ until one binds.
    /// Errors 183 (already registered) and 32 (socket in use) mean "next port". Error 5 (access
    /// denied) is retried once on 127.0.0.1; if that is denied too, the machine needs a URL ACL and
    /// we stop with state acl-denied.</summary>
    public static class PortAcquirer
    {
        public sealed class Result
        {
            public HttpListener Listener;
            public int Port;
            public string State;      // listening | acl-denied | failed
            public string Error;
            public string Prefix;
        }

        public static Result Acquire(int pid)
        {
            string lastError = null;
            foreach (var port in PortPlan.Candidates(pid))
            {
                foreach (var hostName in new[] { "localhost", "127.0.0.1" })
                {
                    var prefix = "http://" + hostName + ":" + port + "/";
                    var l = new HttpListener();
                    l.Prefixes.Add(prefix);
                    l.IgnoreWriteExceptions = true;
                    try
                    {
                        l.Start();
                        return new Result { Listener = l, Port = port, State = "listening", Prefix = prefix };
                    }
                    catch (HttpListenerException ex)
                    {
                        lastError = "port " + port + " (" + hostName + "): " + ex.ErrorCode + " " + ex.Message;
                        try { l.Close(); } catch { }
                        if (ex.ErrorCode == 5)
                        {
                            if (hostName == "127.0.0.1")
                                return new Result { Port = port, State = "acl-denied", Error = lastError, Prefix = prefix };
                            continue;   // retry the same port on 127.0.0.1
                        }
                        break;          // 183 / 32 / anything else: next port
                    }
                    catch (Exception ex)
                    {
                        lastError = "port " + port + ": " + ex.Message;
                        try { l.Close(); } catch { }
                        break;
                    }
                }
            }
            return new Result { State = "failed", Error = lastError ?? "no port in range could be bound" };
        }
    }
}
