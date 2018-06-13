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

        // Serviço, Servidor        
        private List<KeyValuePair<string, string>> mServices = new List<KeyValuePair<string, string>>();
        // Serviço, nextServer
        private Dictionary<string, int> mNextServer = new Dictionary<string, int>();
        // Sessão, Servidor
        private List<KeyValuePair<string, string>> mSessions = new List<KeyValuePair<string, string>>();

        private void Regist(String aAddress, int aPort, string aService )
        {
            if (mServers.Any(p => p.Key == aAddress && p.Value == aPort)) return;
            mServers.Add(new KeyValuePair<string, int>(aAddress, aPort));
            Host.Logger.WriteLine("Added server {0}:{1}.", aAddress, aPort);
            
            // Adicionar Serviços
            if (aService != "")
            {
                var server = string.Concat(aAddress, ":", aPort);
                // Verifica se o Servidor já existe
                var services = mServices.Where(item => item.Value.Equals(server));
                // Se existir retorna
                if (services.Count() > 0) return;
                // Se não, adiciona
                mServices.Add(new KeyValuePair<string, string>(aService, server));
                Host.Logger.WriteLine("Added Service {0} @ {1}.", aService, server);

                // Next Server
                if (!mNextServer.ContainsKey(aService))
                {
                    mNextServer.Add(aService, 0);
                }
            }
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

            string server = string.Concat(aAddress, ":", aPort);

            // Remover Serviço Associado ao Servidor
            try
            {
                var service = mServices.FirstOrDefault(item => item.Value.Equals(server));
                mServices.Remove(service);
                Host.Logger.WriteLine("Removed Service @ {0}", server);
            }
            catch (Exception e)
            {
                Host.Logger.WriteLine("No services @ {0}", server);
            }

            // Remover Sessões Associadas ao Servidor
            mSessions.RemoveAll(item => item.Value.Equals(server));
        }

        #endregion

        #region IArchServerModulePlugIn Members

        public bool Process(IHttpRequest aRequest, IHttpResponse aResponse, IHttpSession aSession)
        {            

            if (mServers.Count < 1) return false;
            if (mServices.Count < 1) return false;

            var url_parts = aRequest.Uri.AbsolutePath.Split('/');
            if (url_parts.Length < 2) return false;

            // Check Service
            var service = url_parts[1];
            var services = mServices.Where(item => item.Key.Equals(service));
            if (services.Count() < 1) return false;

            var proxy_url = new StringBuilder();
            string proxy_server;
            string proxy_service;
            var random = new Random();

            // Verificar se existe sessão
            string session_server = null;
            string sessionID = null;
            
            if (aRequest.Headers["Cookie"] != null)
            {
                sessionID = aRequest.Headers["Cookie"].Substring(aRequest.Headers["Cookie"].IndexOf("__tiny_sessid=") + 14, 36);
            }

            if (sessionID != null)
            {
                var cookieSessions = mSessions.Where(item => item.Key.Equals(sessionID));
                if (cookieSessions.Count() > 0)
                {
                    session_server = cookieSessions.ElementAt(0).Value;
                }
            }
            
            // Se houver sessão, enviar o cliente para o servidor da sessão
            if (session_server != null)
            {
                // Há sessão/servidor
                proxy_service = service;
                proxy_server = session_server;
            }
            else
            {
                // não há sessão, enviar para o próximo
                mNextServer[service] = (mNextServer[service] + 1) % services.Count();
                int nextService = mNextServer[service];
                proxy_service = services.ElementAt(nextService).Key;
                proxy_server = services.ElementAt(nextService).Value;
            }

            proxy_url.AppendFormat("http://{0}", proxy_server);
            var uri = aRequest.Uri.AbsolutePath;
            var proxy_uri = uri.Substring(uri.IndexOf(proxy_service) + proxy_service.Length);
            proxy_url.Append(proxy_uri);

            WebClient client = new WebClient();
            byte[] bytes = null;
            var response = "";

            ForwardCookie(client, aRequest);
            proxy_url.Append(GetQueryString(aRequest));

            if (aRequest.Method == Method.Post)
            {
                try
                {
                    bytes = client.UploadValues(proxy_url.ToString(), GetFormValues(aRequest));
                }
                catch (Exception e)
                {
                    return false;
                }
            }
            else
            {
                try
                {
                    bytes = client.DownloadData(proxy_url.ToString());
                }
                catch (Exception e)
                {
                    return false;
                }
            }

            BackwardCookie(client, aResponse);
            // Se houver nova sessão, guardar para mais tarde enviar para o servidor certo
            if (client.ResponseHeaders["Set-Cookie"] != null)
            {
                string setSessionID = client.ResponseHeaders["Set-Cookie"].Substring(client.ResponseHeaders["Set-Cookie"].IndexOf("__tiny_sessid=") + 14, 36);
                mSessions.Add(new KeyValuePair<string, string>(setSessionID, proxy_server));
            }

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
                    response = response.Insert(match.Index + 8 + (offset * (proxy_service.Length + 1)), String.Concat("/", proxy_service));
                    offset++;
                }

                // src=
                offset = 0;
                foreach (Match match in Regex.Matches( response, @"\bsrc="))
                {
                    response = response.Insert(match.Index + 5 + (offset * (proxy_service.Length + 1)), String.Concat("/", proxy_service));
                    offset++;
                }

                // href=
                offset = 0;
                foreach (Match match in Regex.Matches(response, @"\bhref="))
                {
                    response = response.Insert(match.Index + 6 + (offset * (proxy_service.Length + 1)), String.Concat("/", proxy_service));
                    offset++;
                }

                writer.Write(response);
                writer.Flush();
                return true;
            }

            var writerContent = new StreamWriter(aResponse.Body, client.Encoding);
            writerContent.Write(client.Encoding.GetString(bytes));
            writerContent.Flush();
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

        public string Description => "Proxy clients to the proper server";

        public string Author => "Adriana e Jonathan";

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
