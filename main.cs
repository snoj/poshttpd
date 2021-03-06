using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Threading;
using System.Xml;

namespace DotHttpd {
	public class Program {
		public static void Main(string[] args) {
			Console.WriteLine("Starting poshttpd...");
			
			BG main = new BG();
			string conffile = @".\poshttpd.config";
			if(System.IO.File.Exists(args[0])) {
				conffile = args[0];
				
			}
			
			main = new BG(conffile);
			main.Start();
			
		}
	}
	
	public class HLState {
		public readonly Guid ID = Guid.NewGuid();
	}
	
	public class BG {
		public string prefix = "http://+:81/";
		string rootDir;
		HttpListener _listener = new HttpListener();
		Thread t;
		int asyncLimit = 5;
		List<IAsyncResult> async = new List<IAsyncResult>();
		
		public Dictionary<string, IEngine> plugins = new Dictionary<string,IEngine>();
		public Dictionary<string, object> options = new Dictionary<string,object>();
		
		public BG() {
			this._listener.Prefixes.Add(this.prefix);
			this.t = new Thread(new ThreadStart(this.Looper));
			
			this.plugins.Add(".ps1", new PS());
			this.plugins.Add("*", new PassThru());
		}
		
		public BG(string conffile) : this() {
			System.Xml.XmlDocument xmld = new System.Xml.XmlDocument();
			xmld.Load(conffile);
			this.prefix = xmld.SelectSingleNode("/root/instance/prefix").Attributes["val"].Value.ToString();
			this.rootDir = xmld.SelectSingleNode("/root/instance/rootDir").Attributes["val"].Value.ToString();
			Path.rootDir = this.rootDir;
			
			#region auth config
			foreach(XmlNode n in xmld.SelectNodes("/root/instance/auth")) {
				auth a = new auth();
				a.path = String.Format("/{0}/", n.Attributes["url"].Value.ToString().Trim("\\/".ToCharArray()));
				foreach(string t in n.Attributes["order"].Value.ToString().Split(',')) {
					a.types.Add((AuthenticationSchemes)Enum.Parse(typeof(AuthenticationSchemes), t, true));
				}
				foreach(XmlNode nu in n.SelectNodes("./user")) {
					authUser tu = new authUser() {
						name = nu.Attributes["name"].Value.ToString(),
						password = nu.Attributes["password"].Value.ToString(),
					};
					try {
						tu.forced = (AuthenticationSchemes)Enum.Parse(typeof(AuthenticationSchemes), nu.Attributes["force"].Value.ToString(), true);
					} catch {}
					
					a.users.Add(tu);
				}
				this.protectedDirs.Add(a);
			}
			#endregion
			
			#region default files
			this.options["defaultFiles"] = new List<string>();
			((List<string>)this.options["defaultFiles"]).AddRange(xmld.SelectSingleNode("/root/instance/opt").Attributes["val"].Value.ToString().Split(';'));
			Path.defaultFiles["/"] = (List<string>)this.options["defaultFiles"];
			#endregion
		}
		
		public void Looper() {
			while(t.IsAlive) {
				//if(async.Count(i => i.IsCompleted) <= this.asyncLimit) 
				while(async.Where(i => !i.IsCompleted).ToList().Count >= this.asyncLimit) {
					//Console.Write(".");
					Thread.Sleep(1);
				}
				#region clear out async list
				List<IAsyncResult> tmp = new List<IAsyncResult>();
				foreach(IAsyncResult a in async) {
					if(!a.IsCompleted) {
						tmp.Add(a);
					}
				}
				async.AddRange(tmp);
				#endregion
				
				//this.cleanAuthAttempts();
				
				HLState state = new HLState();
				//Console.WriteLine("creating HLState.ID: {0}", state.ID);
				
				//_listener.GetContext()
				try {
					async.Add(_listener.BeginGetContext(new AsyncCallback(this.cb), state));
					/*Console.WriteLine("Waiting on incoming...");
					IAsyncResult r = _listener.BeginGetContext(new AsyncCallback(this.cb), state);
					while(!r.IsCompleted) {
						Console.Write(".");
						Thread.Sleep(10);
					}*/
				} catch(Exception e) {
					Console.WriteLine(e);
				}
			}
		}
		
		protected void cb(HttpListenerContext con) {
			//Console.WriteLine("Request Trace ID: {0}", con.Request.RequestTraceIdentifier);
			EngineResult s = new EngineResult();//= (System.IO.Stream)new System.IO.MemoryStream(0);
			System.IO.FileInfo f = Path.getABSPath(con.Request.Url);
			
			try {
				Console.WriteLine((con.User != null)?con.User.Identity.Name:"no user");
				//TODO: rewrite this to make use of authAttempts to forbid access.
				if(this.pathRequiresAuth(con.Request.RawUrl) != null && !this.validateAuth(con)) {
					con.Response.StatusCode = 401;
					
					if(this.authAttempts.Count(aa => aa.requestTrace == con.Request.RequestTraceIdentifier) > 3) {
						con.Response.StatusCode = 403;
					} else {
						this.authAttempts.Add(new authAttempt(con));
					}
					Console.WriteLine("{0} error for {1}", con.Response.StatusCode, con.Request.RequestTraceIdentifier);
					s = getErrorPage(con.Response.StatusCode);
				} else {
					s = getPage(con);
					//Console.WriteLine("Content: {0}\r\nLength: {1}", con.Request.RawUrl, s.output.Length);
					con.Response.StatusCode = s.ResponseCode;
					con.Response.ContentLength64 = s.output.Length;
					if(s.ContentType != null) {
						con.Response.ContentType = s.ContentType;
					}
					while(s.output.Position < s.output.Length) {
						byte[] buf = new byte[1024];
						int bRead = s.output.Read(buf, 0, buf.Length);
						con.Response.OutputStream.Write(buf, 0, bRead);
					}
				}
			} catch(Exception e) {
				con.Response.StatusCode = 500;
				Console.WriteLine(e);
			}
			finally {
				var username = (con.User != null)?con.User.Identity.Name:"";
				Console.WriteLine("{0} - {1} - [{2}] {3} {4} {5}", con.Request.RemoteEndPoint.ToString(), username, DateTime.Now, con.Response.StatusCode, con.Request.HttpMethod, con.Request.RawUrl);
				s.output.Close();
				con.Response.Close();
				
			}
		}
		protected void cb(IAsyncResult result) {
			HttpListenerContext con = _listener.EndGetContext(result);
			//Console.WriteLine("CB HLState.ID: {0}", ((HLState)result.AsyncState).ID);
			cb(con);
		}
		
		protected EngineResult getErrorPage(int statusCode) {
			if(statusCode >= 200 && statusCode < 300) {
				//Console.WriteLine("wth?! why are you getting an error page for 2xx?!");
				throw new Exception("wth?! why are you getting an error page for 2xx?!");
			}
			
			string efile = String.Format(@".\errors\{0}.html", statusCode);
			if(System.IO.File.Exists(efile)) {
				return new EngineResult() {
					output = (System.IO.Stream)new System.IO.FileStream(efile, System.IO.FileMode.Open, System.IO.FileAccess.Read),
					ResponseCode = statusCode,
					ContentType = "text/html"
				};
			}
			//Console.WriteLine("Error code file not found, prolly should die somehow.");
			throw new Exception("Error code file not found, prolly should die somehow.");
		}
		
		protected EngineResult getPage(HttpListenerContext context) {
			EngineResult s = new EngineResult();
			System.IO.FileInfo f = Path.getABSPath(context.Request.Url);
			try {
				
				try {
					s = this.plugins[f.Extension].run(context);
				} catch {
					s = this.plugins["*"].run(context);
				}
				if(!(s.ResponseCode >= 200 && s.ResponseCode < 300)) {
					throw new Exception("invalid request.");
				}
			} catch(Exception e) {
				//Console.WriteLine("error fetching {0} -- Code: {1}", f.FullName, s.ResponseCode);
				//Console.WriteLine(e);
				return this.getErrorPage(s.ResponseCode);
			} finally {
				//something?
			}
			return s;
		}
		
		protected List<authAttempt> authAttempts = new List<authAttempt>();
		
		protected void cleanAuthAttempts() {
			try {
				List<authAttempt> t = this.authAttempts.Where(f => f.timestamp < DateTime.Now.AddMinutes(-10)).ToList();
				
				lock(this) {
					this.authAttempts = t;
				}
			} catch {}
			
		}
		
		protected List<auth> protectedDirs = new List<auth>();
		protected auth pathRequiresAuth(string RawUrl) {
			foreach(auth s in this.protectedDirs) {
				if(RawUrl.ToLower().StartsWith(s.path.ToLower())) {
					return s;
				}
			}
			
			return null;
		}
		
		protected bool userRequiresAuth(HttpListenerContext context) {
			return pathRequiresAuth(context.Request.RawUrl) != null && context.Request.IsAuthenticated == false;
		}
		
		protected bool validateAuth(HttpListenerContext context) {
			//get target auth
			//validate user creds
			
			//HttpListenerBasicIdentity id = (HttpListenerBasicIdentity)context.User.Identity;
			//Console.WriteLine("username: {0} password: {1} isValid: {2}", id.Name, id.Password, pathRequiresAuth(context.Request.RawUrl).isValid(id));
		
			try {
				return pathRequiresAuth(context.Request.RawUrl).isValid(context.User.Identity);
			} catch {  }
			
			return false;
		}
		
		protected AuthenticationSchemes checkAuth(HttpListenerRequest request) {
			try {
				return pathRequiresAuth(request.RawUrl).types_c;
			} catch {
				return AuthenticationSchemes.Anonymous;
			}
			
		}
		
		public void Start() {
			this._listener.Start();
			_listener.AuthenticationSchemeSelectorDelegate = new AuthenticationSchemeSelector(this.checkAuth);
			this.t.Start();
		}
		
		public void Stop() {
			this._listener.Close();
			this.t.Abort();
		}
	}
	
	#region auth
	public class auth {
		public string path;
		public List<AuthenticationSchemes> types = new List<AuthenticationSchemes>();
		public AuthenticationSchemes types_c {
			get {
				AuthenticationSchemes rtn = this.types[0];
				foreach(AuthenticationSchemes t in this.types) {
					rtn = rtn | t;
				}
				return rtn;
			}
		}
		
		public bool isValid(System.Security.Principal.IIdentity id) {
			switch(id.GetType().FullName) {
				case "System.Net.HttpListenerBasicIdentity":
					HttpListenerBasicIdentity id2 = (HttpListenerBasicIdentity)id;
					foreach(authUser a in this.users) {
						if(a.name == id2.Name &&
							a.password == id2.Password
							) {
							if(a.forced != null && a.forced == (AuthenticationSchemes)Enum.Parse(typeof(AuthenticationSchemes), id2.AuthenticationType, true)) {
								return (a.forced != null && a.forced == (AuthenticationSchemes)Enum.Parse(typeof(AuthenticationSchemes), id2.AuthenticationType, true));
							}
							return true;
						}
					}
					break;
				case "System.Security.Principal.WindowsIdentity":
					System.Security.Principal.WindowsIdentity wid = (System.Security.Principal.WindowsIdentity)id;
										
					if(this.users.Count(f => f.forced != null && f.forced == AuthenticationSchemes.Ntlm) > 0) {
						return this.users.Where(f => f.name.ToLower() == wid.Name.ToLower() && ((f.forced == null)?true:f.forced == (AuthenticationSchemes)Enum.Parse(typeof(AuthenticationSchemes), wid.AuthenticationType, true))).Count() > 0 && wid.IsAuthenticated;
					} else {
						return wid.IsAuthenticated;
					}
					break;
			}
			return false;
		}
		public List<authUser> users = new List<authUser>();
	}
	
	public class authUser {
		public string name;
		public string password;
		public AuthenticationSchemes forced;
	}
	
	public class authAttempt {
		public DateTime timestamp;
		public Guid requestTrace;
		public IPEndPoint remote;
		
		/*public List<AuthenticationSchemes> types = new List<AuthenticationSchemes>();
		public AuthenticationSchemes types_c {
			get {
				AuthenticationSchemes rtn = this.types[0];
				foreach(AuthenticationSchemes t in this.types) {
					rtn = rtn | t;
				}
				return rtn;
			}
		}*/
		public AuthenticationSchemes type = AuthenticationSchemes.Anonymous;
		
		public authAttempt(HttpListenerContext con) {
			this.timestamp = DateTime.Now;
			this.requestTrace = con.Request.RequestTraceIdentifier;
			this.remote = (IPEndPoint)con.Request.RemoteEndPoint;
			this.type = (AuthenticationSchemes)Enum.Parse(typeof(AuthenticationSchemes), con.User.Identity.AuthenticationType, true);
		}
	}
	
	#endregion
	
	#region tools
	
	//TODO: Make this instance instead of static.
	class Path {
		public static string rootDir = "";
		public static Dictionary<string, List<string>> defaultFiles = new Dictionary<string,List<string>>();
		
		static System.IO.FileInfo getPath(Uri url) {
			/*string fp = RawUrl;
			if(fp.Contains("?")) {
				fp = fp.Substring(0, fp.IndexOf("?"));
			}*/
			string fp = url.AbsolutePath;
			return new System.IO.FileInfo(fp);
		}
		public static System.IO.FileInfo getABSPath(Uri url) {
			/*string fp = RawUrl;
			if(fp.Contains("?")) {
				fp = fp.Substring(0, fp.IndexOf("?"));
			}*/
			
			string fp = url.AbsolutePath;
			string tfp = System.IO.Path.Combine(new string[] {Path.rootDir, fp.TrimStart("\\/".ToCharArray())});
			if(fp.EndsWith("/") || System.IO.Directory.Exists(tfp)) {
				//test for default files.
				foreach(KeyValuePair<string, List<string>> ap in Path.defaultFiles.Where(f => fp.StartsWith(f.Key)).ToArray()) {
					foreach(string d in ap.Value) {
						if(System.IO.File.Exists(System.IO.Path.Combine(new string[] {Path.rootDir, fp.TrimStart("\\/".ToCharArray()), d}))) {
							tfp = System.IO.Path.Combine(new string[] {Path.rootDir, fp.TrimStart("\\/".ToCharArray()), d});
							break;
						}
					}
				}
			}
			
			return new System.IO.FileInfo(tfp); //new System.IO.FileInfo(System.IO.Path.Combine(new string[] {Path.rootDir, fp.TrimStart("\\/".ToCharArray())}));
		}
	}
	
	class HttpListenerResponseClone {
		public HttpListenerResponse Response;
		public HttpListenerResponseClone(HttpListenerResponse Response) {
			this.Response = Response;
		}
		
		public HttpListenerResponse Clone() {
			return ((HttpListenerResponseClone)this.MemberwiseClone()).Response;
		}
	}
	
	#endregion
	
	#region engines
	public interface IEngine {
		EngineResult run(HttpListenerContext context);
		//HttpListenerResponse getResponse();
	}
	
	public class EngineResult {
		public System.IO.Stream output = (System.IO.Stream)new System.IO.MemoryStream(0);
		public int ResponseCode = 0;
		public string ContentType = null;
	}
	
	///<Summary>
	///
	///</Summary>
	public class PS : IEngine {
		string rootDir = @"";
		//HttpListenerResponse nresponse;
		public EngineResult run(HttpListenerContext context) {
			try {
				PowerShell shell = PowerShell.Create();
				
				shell.AddCommand(Path.getABSPath(context.Request.Url).FullName);
				
				//doesn't work.
				//HttpListenerResponseClone tmp = new HttpListenerResponseClone(context.Response);
				//nresponse = tmp.Clone();
				
				//shell.AddParameter("Request", context.Request);
				//shell.AddParameter("Response", nresponse);
				//shell.AddParameter("User", context.User);
				
				shell.AddParameter("Context", context);
				
				System.Collections.ObjectModel.Collection<PSObject> rtn = shell.Invoke();
				
				System.IO.MemoryStream ms = new System.IO.MemoryStream();
				
				
				foreach(PSObject r in rtn) {
					if(r.BaseObject.GetType().Equals(typeof(string))) {
						byte[] buf = System.Text.Encoding.UTF8.GetBytes(r.ToString() + Environment.NewLine);
						ms.Write(buf, 0 , buf.Length);
					}
					
					//if(r.BaseObject.Equals(typeof(HttpListenerResponse))) {
						//nresponse = (HttpListenerResponse)r.BaseObject;
					//}
				}
				ms.Seek(0, System.IO.SeekOrigin.Begin);
				
				//return (System.IO.Stream)ms;
				return new EngineResult() {
					output = (System.IO.Stream)ms,
					ResponseCode = 200
				};
			} catch(Exception e) {
				//Console.WriteLine(e);
			}
			
			return new EngineResult() {
				output = (System.IO.Stream)new System.IO.MemoryStream(0),
				ResponseCode = 404
			};
		}
		
		/*public HttpListenerResponse getResponse(){
			return this.nresponse;
		}*/
	}
	
	///<Summary>
	///	Passthru requested files. This will find the file on disk and send it to the client.
	/// Useful for static things like HTML files, images, and the like.
	///</Summary>
	public class PassThru : IEngine {
		string rootDir = @"";
		//HttpListenerResponse nresponse;
		public EngineResult run(HttpListenerContext context) {
			//Console.WriteLine(context.Request.RawUrl);
			//this.nresponse = context.Response;
			/*string fp = context.Request.RawUrl;
			if(fp.Contains("?")) {
				fp = fp.Substring(0, fp.IndexOf("?"));
			}*/
			
			string absfp = Path.getABSPath(context.Request.Url).FullName; //System.IO.Path.Combine(new string[] { this.rootDir, fp.Trim('/') });
			//Console.WriteLine("Checking {0}", absfp);
			if(System.IO.File.Exists(absfp)) {
				try {
					//Console.WriteLine("Opening {0}", absfp);
					//return (System.IO.Stream)new System.IO.FileStream(absfp, System.IO.FileMode.Open, System.IO.FileAccess.Read);
					return new EngineResult() {
						output = (System.IO.Stream)new System.IO.FileStream(absfp, System.IO.FileMode.Open, System.IO.FileAccess.Read),
						ResponseCode = 200
					};
				} catch(Exception e) {
					return new EngineResult() {
						output = (System.IO.Stream)new System.IO.MemoryStream(0),
						ResponseCode = 404
					};
				}
			}
			
			return new EngineResult() {
				output = (System.IO.Stream)new System.IO.MemoryStream(0),
				ResponseCode = 404
			};
		}
		
		
		/*public HttpListenerResponse getResponse(){
			return this.nresponse;
		}*/
	}
	#endregion
}