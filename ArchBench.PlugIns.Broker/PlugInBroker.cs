using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using HttpServer;
using HttpServer.Sessions;
using System.Net.Sockets;
using System.Linq;
using System.IO;
using System.Collections.Specialized;
using System.Text.RegularExpressions;

namespace ArchBench.PlugIns.Broker
{
    public class PlugInBroker : IArchServerModulePlugIn
    {
        private readonly TcpListener mListener;
        private Thread mRegisterThread;

        public PlugInBroker()
        {
            mListener = new TcpListener(IPAddress.Any, 9000);
        }

        #region Regist/Unregist servers

        private void ReceiveThreadFunction()
        {
            try
            {
                // Start listening for client requests.
                mListener.Start();

                // Buffer for reading data
                Byte[] bytes = new Byte[256];

                // Enter the listening loop.
                while (true)
                {
                    // Perform a blocking call to accept requests.
                    // You could also user server.AcceptSocket() here.
                    TcpClient client = mListener.AcceptTcpClient();

                    // Get a stream object for reading and writing
                    NetworkStream stream = client.GetStream();

                    int count = stream.Read(bytes, 0, bytes.Length);
                    if (count != 0)
                    {
                        // Translate data bytes to a ASCII string.
                        String data = Encoding.ASCII.GetString(bytes, 0, count);

                        string[] parts = data.Split( '|' );
                        System.Diagnostics.Trace.Assert( parts.Length > 2 );
                        char operation = parts[0][0];
                        String server  = parts[1];
                        String port    = parts[2];
                        string service = parts.Length > 2 ? parts[3] : string.Empty;
                        switch (operation)
                        {
                            case '+':
                                Regist(server, int.Parse(port), service);
                                break;
                            case '-':
                                Unregist(server, int.Parse(port));
                                break;
                        }

                    }

                    client.Close();
                }
            }
            catch (SocketException e)
            {
                Host.Logger.WriteLine("SocketException: {0}", e);
            }
            finally
            {
                mListener.Stop();
            }
        }

        private readonly List<KeyValuePair<string, int>> mServers = new List<KeyValuePair<string, int>>();

        // Serviço,Servidor
        //Dictionary<string, ICollection<string>> mServices = new Dictionary<string, ICollection<string>();
        // Sessão,Servidor
        //Dictionary<string, string> mSessions = new Dictionary<string, string>();
        
        private void Regist(String aAddress, int aPort, string aService )
        {
            if (mServers.Any(p => p.Key == aAddress && p.Value == aPort)) return;
            mServers.Add(new KeyValuePair<string, int>(aAddress, aPort));
            Host.Logger.WriteLine("Added server {0}:{1}.", aAddress, aPort);
            
            /*
            if (aService != "" && mServices.ContainsKey(aService)) {
                String[] servers = mServices[aService];
                servers.Add(string.Concat(aAddress, ":", aPort));
                //mServices.Add(string.Concat(aAddress, ":", aPort), aService);
                Host.Logger.WriteLine("Added Service {0} @ {1}.", aService, string.Concat(aAddress, ":", aPort));
            }*/
        }

        private void Unregist(string aAddress, int aPort)
        {
            if (mServers.Remove(new KeyValuePair<string, int>(aAddress, aPort)))
            {
                Host.Logger.WriteLine("Removed server {0}:{1}.", aAddress, aPort);
            }
            else
            {
                Host.Logger.WriteLine("The server {0}:{1} is not registered.", aAddress, aPort);
            }

            // Remover Serviços
            /*
            if (mServices.Remove(mServices.First(item => item.Key.Equals(string.Concat(aAddress, ":", aPort)))))
            {
                Host.Logger.WriteLine("Removed Service @ {0}", string.Concat(aAddress, ":", aPort));
            }*/
        }

        #endregion

        #region IArchServerModulePlugIn Members

        public bool Process(IHttpRequest aRequest, IHttpResponse aResponse, IHttpSession aSession)
        {            

            if (mServers.Count < 1) return false;

            /*
            var url_parts = aRequest.Uri.AbsolutePath.Split('/');
            if (url_parts.Length < 2) return false;

            // Check Service
            var service = url_parts[1];
            var services = mServices.Where(item => item.Value.Equals(service));
            if (services.Count() < 1) return false;

            // Check Session
            var writer5 = new StreamWriter(aResponse.Body);
            writer5.WriteLine("Broker Home Page, Service {0}", service);
            writer5.Flush();
            return true;
            // TODO Query String
            */

            var proxy = false;
            var proxy_url = new StringBuilder();
            var proxy_server = "";

            if (aRequest.Uri.AbsolutePath.StartsWith("/info"))
            {
                proxy = true;
                proxy_server = "/info";
                proxy_url.AppendFormat("http://{0}:{1}", mServers[0].Key, mServers[0].Value);
                var uri = aRequest.Uri.AbsolutePath;
                var proxy_uri = uri.Substring(uri.IndexOf("/info") + 5);
                proxy_url.Append(proxy_uri);
            }

            if (aRequest.Uri.AbsolutePath.StartsWith("/sidoc"))
            {
                proxy = true;
                proxy_server = "/sidoc";
                proxy_url.AppendFormat("http://{0}:{1}", mServers[1].Key, mServers[1].Value);
                var uri = aRequest.Uri.AbsolutePath;
                var proxy_uri = uri.Substring(uri.IndexOf("/sidoc") + 6);
                proxy_url.Append(proxy_uri);
            }

            if (proxy)
            {
                WebClient client = new WebClient();
                byte[] bytes = null;
                var response = "";

                ForwardCookie(client, aRequest);
                proxy_url.Append(GetQueryString(aRequest)); // ainda não funciona

                if (aRequest.Method == Method.Post)
                {
                    bytes = client.UploadValues(proxy_url.ToString(), GetFormValues(aRequest));
                }
                else
                {
                    bytes = client.DownloadData(proxy_url.ToString());
                }

                BackwardCookie(client, aResponse);

                // Pass Content-Type
                aResponse.AddHeader("Content-Type", client.ResponseHeaders["Content-Type"]);

                if (client.ResponseHeaders["Content-Type"] == "text/html;charset=UTF-8")
                {
                    var writer = new StreamWriter(aResponse.Body);
                    response = System.Text.Encoding.Default.GetString(bytes);
                    var offset = 0;
                    
                    // action=
                    foreach (Match match in Regex.Matches(response, @"\baction="))
                    {
                        response = response.Insert(match.Index + 8 + (offset * proxy_server.Length), proxy_server);
                        offset++;
                    }

                    // src=
                    offset = 0;
                    foreach (Match match in Regex.Matches( response, @"\bsrc="))
                    {
                        response = response.Insert(match.Index + 5 + (offset * proxy_server.Length), proxy_server);
                        offset++;
                    }

                    // href=
                    offset = 0;
                    foreach (Match match in Regex.Matches(response, @"\bhref="))
                    {
                        response = response.Insert(match.Index + 6 + (offset * proxy_server.Length), proxy_server);
                        offset++;
                    }

                    writer.Write(response);
                    writer.Flush();
                    return true;
                }

                var writer3 = new StreamWriter(aResponse.Body, client.Encoding);
                writer3.Write(client.Encoding.GetString(bytes));
                writer3.Flush();
                return true;
                /*
                aResponse.SendHeaders();
                aResponse.SendBody(bytes, 0, bytes.Length);
                return true;
                */

            }

            var writer2 = new StreamWriter(aResponse.Body);
            writer2.WriteLine("Broker Home Page");
            writer2.Flush();
            return true;

        }

        private NameValueCollection GetFormValues(IHttpRequest aRequest)
        {
            NameValueCollection values = new NameValueCollection();
            foreach (HttpInputItem item in aRequest.Form)
            {
                values.Add(item.Name, item.Value);
            }
            return values;
        }

        private string GetQueryString(IHttpRequest aRequest)
        {
            int count = aRequest.QueryString.Count();
            if (count == 0) return "";

            var parameters = new StringBuilder("?");
            foreach (HttpInputItem item in aRequest.QueryString)
            {
                parameters.Append(String.Format("{0}={1}", item.Name, item.Value));
                if (--count > 0) parameters.Append('&');
            }
            return parameters.ToString();
        }

        private void ForwardCookie(WebClient aClient, IHttpRequest aRequest)
        {
            if (aRequest.Headers["Cookie"] == null) return;
            aClient.Headers.Add("Cookie", aRequest.Headers["Cookie"]);
        }

        private void BackwardCookie(WebClient aClient, IHttpResponse aResponse)
        {
            if (aClient.ResponseHeaders["Set-Cookie"] == null) return;
            aResponse.AddHeader("Set-Cookie", aClient.ResponseHeaders["Set-Cookie"]);
        }

        #endregion

        #region IArchServerPlugIn Members

        public string Name => "ArchServer Broker Plugin";

        public string Description => "Dispatch clients to the proper server";

        public string Author => "Leonel Nobrega";

        public string Version => "1.0";

        public bool Enabled { get; set; }

        public IArchServerPlugInHost Host
        {
            get; set;
        }

        public void Initialize()
        {
            mRegisterThread = new Thread(ReceiveThreadFunction);
            mRegisterThread.IsBackground = true;
            mRegisterThread.Start();
        }

        public void Dispose()
        {
        }

        #endregion
    }

}
