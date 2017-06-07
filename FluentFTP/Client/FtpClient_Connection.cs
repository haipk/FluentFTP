using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Globalization;
using System.Security.Authentication;
using System.Net;
using FluentFTP.Proxy;
#if !CORE
using System.Web;
#endif

#if (CORE || NETFX)
using System.Threading;
#endif
#if (CORE || NETFX45)
using System.Threading.Tasks;
#endif

namespace FluentFTP {
	
	/// <summary>
	/// FTP Control Connection. Speaks the FTP protocol with the server and
	/// provides facilities for performing transactions.
	/// 
	/// Debugging problems with FTP transactions is much easier to do when
	/// you can see exactly what is sent to the server and the reply 
	/// FluentFTP gets in return. Please review the Debug example
	/// below for information on how to add <see cref="System.Diagnostics.TraceListener"/>s for capturing
	/// the conversation between FluentFTP and the server.
	/// </summary>
	/// <example>The following example illustrates how to assist in debugging
	/// FluentFTP by getting a transaction log from the server.
	/// <code source="..\Examples\Debug.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates adding a custom file
	/// listing parser in the event that you encounter a list format
	/// not already supported.
	/// <code source="..\Examples\CustomParser.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to validate
	/// a SSL certificate when using SSL/TLS.
	/// <code source="..\Examples\ValidateCertificate.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to download a file.
	/// <code source="..\Examples\OpenRead.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to download a file
	/// using a URI object.
	/// <code source="..\Examples\OpenReadURI.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to upload a file.
	/// <code source="..\Examples\OpenWrite.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to upload a file
	/// using a URI object.
	/// <code source="..\Examples\OpenWriteURI.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to append to a file.
	/// <code source="..\Examples\OpenAppend.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to append to a file
	/// using a URI object.
	/// <code source="..\Examples\OpenAppendURI.cs" lang="cs" />
	/// </example>
	/// <example>The following example demonstrates how to get a file
	/// listing from the server.
	/// <code source="..\Examples\GetListing.cs" lang="cs" />
	/// </example>
	public partial class FtpClient : IDisposable {

		#region Properties

		/// <summary>
		/// Used for internally synchronizing access to this
		/// object from multiple threads
		/// </summary>
		readonly Object m_lock = new Object();

		/// <summary>
		/// For usage by FTP proxies only
		/// </summary>
		protected Object Lock {
			get {
				return m_lock;
			}
		}

		/// <summary>
		/// A list of asynchronous methods that are in progress
		/// </summary>
		readonly Dictionary<IAsyncResult, object> m_asyncmethods = new Dictionary<IAsyncResult, object>();

		/// <summary>
		/// Control connection socket stream
		/// </summary>
		FtpSocketStream m_stream = null;

		bool m_isDisposed = false;
		/// <summary>
		/// Gets a value indicating if this object has already been disposed.
		/// </summary>
		public bool IsDisposed {
			get {
				return m_isDisposed;
			}
			private set {
				m_isDisposed = value;
			}
		}

		/// <summary>
		/// Gets the base stream for talking to the server via
		/// the control connection.
		/// </summary>
		protected Stream BaseStream {
			get {
				return m_stream;
			}
		}

		FtpIpVersion m_ipVersions = FtpIpVersion.ANY;
		/// <summary>
		/// Flags specifying which versions of the internet protocol to
		/// support when making a connection. All addresses returned during
		/// name resolution are tried until a successful connection is made.
		/// You can fine tune which versions of the internet protocol to use
		/// by adding or removing flags here. I.e., setting this property
		/// to FtpIpVersion.IPv4 will cause the connection process to
		/// ignore IPv6 addresses. The default value is ANY version.
		/// </summary>
		public FtpIpVersion InternetProtocolVersions {
			get {
				return m_ipVersions;
			}
			set {
				m_ipVersions = value;
			}
		}

		int m_socketPollInterval = 15000;
		/// <summary>
		/// Gets or sets the length of time in milliseconds
		/// that must pass since the last socket activity
		/// before calling <see cref="System.Net.Sockets.Socket.Poll"/> 
		/// on the socket to test for connectivity. 
		/// Setting this interval too low will
		/// have a negative impact on performance. Setting this
		/// interval to 0 disables Polling all together.
		/// The default value is 15 seconds.
		/// </summary>
		public int SocketPollInterval {
			get { return m_socketPollInterval; }
			set {
				m_socketPollInterval = value;
				if (m_stream != null)
					m_stream.SocketPollInterval = value;
			}
		}

		bool m_staleDataTest = true;
		/// <summary>
		/// Gets or sets a value indicating whether a test should be performed to
		/// see if there is stale (unrequested data) sitting on the socket. In some
		/// cases the control connection may time out but before the server closes
		/// the connection it might send a 4xx response that was unexpected and
		/// can cause synchronization errors with transactions. To avoid this
		/// problem the <see cref="o:Execute"/> method checks to see if there is any data
		/// available on the socket before executing a command. On Azure hosting
		/// platforms this check can cause an exception to be thrown. In order
		/// to work around the exception you can set this property to false
		/// which will skip the test entirely however doing so eliminates the
		/// best effort attempt of detecting such scenarios. See this thread
		/// for more details about the Azure problem:
		/// https://netftp.codeplex.com/discussions/535879
		/// </summary>
		public bool StaleDataCheck {
			get { return m_staleDataTest; }
			set { m_staleDataTest = value; }
		}

		/// <summary>
		/// Gets a value indicating if the connection is alive
		/// </summary>
		public bool IsConnected {
			get {
				if (m_stream != null)
					return m_stream.IsConnected;
				return false;
			}
		}

		bool m_threadSafeDataChannels = false;
		/// <summary>
		/// When this value is set to true (default) the control connection
		/// is cloned and a new connection the server is established for the
		/// data channel operation. This is a thread safe approach to make
		/// asynchronous operations on a single control connection transparent
		/// to the developer.
		/// </summary>
		public bool EnableThreadSafeDataConnections {
			get {
				return m_threadSafeDataChannels;
			}
			set {
				m_threadSafeDataChannels = value;
			}
		}

		bool m_isClone = false;
		/// <summary>
		/// Gets a value indicating if this control connection is a clone. This property
		/// is used with data streams to determine if the connection should be closed
		/// when the stream is closed. Servers typically only allow 1 data connection
		/// per control connection. If you try to open multiple data connections this
		/// object will be cloned for 2 or more resulting in N new connections to the
		/// server.
		/// </summary>
		internal bool IsClone {
			get {
				return m_isClone;
			}
			private set {
				m_isClone = value;
			}
		}

		Encoding m_textEncoding = Encoding.ASCII;
		bool m_textEncodingAutoUTF = true;
		/// <summary>
		/// Gets or sets the text encoding being used when talking with the server. The default
		/// value is <see cref="System.Text.Encoding.ASCII"/> however upon connection, the client checks
		/// for UTF8 support and if it's there this property is switched over to
		/// <see cref="System.Text.Encoding.UTF8"/>. Manually setting this value overrides automatic detection
		/// based on the FEAT list; if you change this value it's always used
		/// regardless of what the server advertises, if anything.
		/// </summary>
		public Encoding Encoding {
			get {
				return m_textEncoding;
			}
			set {
				lock (m_lock) {
					m_textEncoding = value;
					m_textEncodingAutoUTF = false;
				}
			}
		}

		string m_host = null;
		/// <summary>
		/// The server to connect to
		/// </summary>
		public string Host {
			get {
				return m_host;
			}
			set {

				// remove unwanted prefix/postfix
				if (value.StartsWith("ftp://")) {
					value = value.Substring(value.IndexOf("ftp://") + ("ftp://").Length);
				}
				if (value.EndsWith("/")) {
					value = value.Replace("/", "");
				}

				m_host = value;
			}
		}

		int m_port = 0;
		/// <summary>
		/// The port to connect to. If this value is set to 0 (Default) the port used
		/// will be determined by the type of SSL used or if no SSL is to be used it 
		/// will automatically connect to port 21.
		/// </summary>
		public int Port {
			get {
				// automatically determine port
				// when m_port is 0.
				if (m_port == 0) {
					switch (EncryptionMode) {
						case FtpEncryptionMode.None:
						case FtpEncryptionMode.Explicit:
							return 21;
						case FtpEncryptionMode.Implicit:
							return 990;
					}
				}

				return m_port;
			}
			set {
				m_port = value;
			}
		}

		NetworkCredential m_credentials = new NetworkCredential("anonymous", "anonymous");
		/// <summary>
		/// Credentials used for authentication
		/// </summary>
		public NetworkCredential Credentials {
			get {
				return m_credentials;
			}
			set {
				m_credentials = value;
			}
		}

		int m_maxDerefCount = 20;
		/// <summary>
		/// Gets or sets a value that controls the maximum depth
		/// of recursion that <see cref="o:DereferenceLink"/> will follow symbolic
		/// links before giving up. You can also specify the value
		/// to be used as one of the overloaded parameters to the
		/// <see cref="o:DereferenceLink"/> method. The default value is 20. Specifying
		/// -1 here means indefinitely try to resolve a link. This is
		/// not recommended for obvious reasons (stack overflow).
		/// </summary>
		public int MaximumDereferenceCount {
			get {
				return m_maxDerefCount;
			}
			set {
				m_maxDerefCount = value;
			}
		}

		X509CertificateCollection m_clientCerts = new X509CertificateCollection();
		/// <summary>
		/// Client certificates to be used in SSL authentication process
		/// </summary>
		public X509CertificateCollection ClientCertificates {
			get {
				return m_clientCerts;
			}
			protected set {
				m_clientCerts = value;
			}
		}

		// Holds the cached resolved address
		string m_Address;

		Func<string> m_AddressResolver;

		/// <summary>
		/// Delegate used for resolving local address, used for active data connections
		/// This can be used in case you're behind a router, but port forwarding is configured to forward the
		/// ports from your router to your internal IP. In that case, we need to send the router's IP instead of our internal IP.
		/// See example: FtpClient.GetPublicIP -> This uses Ipify api to find external IP
		/// </summary>
		public Func<string> AddressResolver {
			get { return m_AddressResolver; }
			set { m_AddressResolver = value; }
		}

		IEnumerable<int> m_ActivePorts;

		/// <summary>
		/// Ports used for Active Data Connection
		/// </summary>
		public IEnumerable<int> ActivePorts {
			get { return m_ActivePorts; }
			set { m_ActivePorts = value; }
		}

		FtpDataConnectionType m_dataConnectionType = FtpDataConnectionType.AutoPassive;
		/// <summary>
		/// Data connection type, default is AutoPassive which tries
		/// a connection with EPSV first and if it fails then tries
		/// PASV before giving up. If you know exactly which kind of
		/// connection you need you can slightly increase performance
		/// by defining a specific type of passive or active data
		/// connection here.
		/// </summary>
		public FtpDataConnectionType DataConnectionType {
			get {
				return m_dataConnectionType;
			}
			set {
				m_dataConnectionType = value;
			}
		}

		bool m_ungracefullDisconnect = false;
		/// <summary>
		/// Disconnect from the server without sending QUIT. This helps
		/// work around IOExceptions caused by buggy connection resets
		/// when closing the control connection.
		/// </summary>
		public bool UngracefullDisconnection {
			get {
				return m_ungracefullDisconnect;
			}
			set {
				m_ungracefullDisconnect = value;
			}
		}

		int m_connectTimeout = 15000;
		/// <summary>
		/// Gets or sets the length of time in milliseconds to wait for a connection 
		/// attempt to succeed before giving up. Default is 15000 (15 seconds).
		/// </summary>
		public int ConnectTimeout {
			get {
				return m_connectTimeout;
			}
			set {
				m_connectTimeout = value;
			}
		}

		int m_readTimeout = 15000;
		/// <summary>
		/// Gets or sets the length of time wait in milliseconds for data to be
		/// read from the underlying stream. The default value is 15000 (15 seconds).
		/// </summary>
		public int ReadTimeout {
			get {
				return m_readTimeout;
			}
			set {
				m_readTimeout = value;
			}
		}

		int m_dataConnectionConnectTimeout = 15000;
		/// <summary>
		/// Gets or sets the length of time in milliseconds for a data connection
		/// to be established before giving up. Default is 15000 (15 seconds).
		/// </summary>
		public int DataConnectionConnectTimeout {
			get {
				return m_dataConnectionConnectTimeout;
			}
			set {
				m_dataConnectionConnectTimeout = value;
			}
		}

		int m_dataConnectionReadTimeout = 15000;
		/// <summary>
		/// Gets or sets the length of time in milliseconds the data channel
		/// should wait for the server to send data. Default value is 
		/// 15000 (15 seconds).
		/// </summary>
		public int DataConnectionReadTimeout {
			get {
				return m_dataConnectionReadTimeout;
			}
			set {
				m_dataConnectionReadTimeout = value;
			}
		}

		bool m_keepAlive = false;
		/// <summary>
		/// Gets or sets a value indicating if <see cref="System.Net.Sockets.SocketOptionName.KeepAlive"/> should be set on 
		/// the underlying stream's socket. If the connection is alive, the option is
		/// adjusted in real-time. The value is stored and the KeepAlive option is set
		/// accordingly upon any new connections. The value set here is also applied to
		/// all future data streams. It has no affect on cloned control connections or
		/// data connections already in progress. The default value is false.
		/// </summary>
		public bool SocketKeepAlive {
			get {
				return m_keepAlive;
			}
			set {
				m_keepAlive = value;
				if (m_stream != null)
					m_stream.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, value);
			}
		}

		FtpCapability m_caps = FtpCapability.NONE;
		/// <summary>
		/// Gets the server capabilities represented by flags
		/// </summary>
		public FtpCapability Capabilities {
			get {
				if (m_stream == null || !m_stream.IsConnected) {
					Connect();
				}

				return m_caps;
			}
			protected set {
				m_caps = value;
			}
		}

		FtpHashAlgorithm m_hashAlgorithms = FtpHashAlgorithm.NONE;
		/// <summary>
		/// Get the hash types supported by the server, if any. This
		/// is a recent extension to the protocol that is not fully
		/// standardized and is not guaranteed to work. See here for
		/// more details:
		/// http://tools.ietf.org/html/draft-bryan-ftpext-hash-02
		/// </summary>
		public FtpHashAlgorithm HashAlgorithms {
			get {
				if (m_stream == null || !m_stream.IsConnected) {
					Connect();
				}

				return m_hashAlgorithms;
			}
			private set {
				m_hashAlgorithms = value;
			}
		}

		FtpEncryptionMode m_encryptionmode = FtpEncryptionMode.None;
		/// <summary>
		/// Type of SSL to use, or none. Default is none. Explicit is TLS, Implicit is SSL.
		/// </summary>
		public FtpEncryptionMode EncryptionMode {
			get {
				return m_encryptionmode;
			}
			set {
				m_encryptionmode = value;
			}
		}

		bool m_dataConnectionEncryption = true;
		/// <summary>
		/// Indicates if data channel transfers should be encrypted. Only valid if <see cref="EncryptionMode"/>
		/// property is not equal to <see cref="FtpEncryptionMode.None"/>.
		/// </summary>
		public bool DataConnectionEncryption {
			get {
				return m_dataConnectionEncryption;
			}
			set {
				m_dataConnectionEncryption = value;
			}
		}

#if CORE
		private SslProtocols m_SslProtocols = SslProtocols.Tls11 | SslProtocols.Ssl3;
#else
		private SslProtocols m_SslProtocols = SslProtocols.Default;
#endif
		/// <summary>
		/// Encryption protocols to use. Only valid if EncryptionMode property is not equal to <see cref="FtpEncryptionMode.None"/>.
		/// Default value is .NET Framework defaults from the <see cref="System.Net.Security.SslStream"/> class.
		/// </summary>
		public SslProtocols SslProtocols {
			get {
				return m_SslProtocols;
			}
			set {
				m_SslProtocols = value;
			}
		}

		FtpSslValidation m_sslvalidate = null;
		/// <summary>
		/// Event is fired to validate SSL certificates. If this event is
		/// not handled and there are errors validating the certificate
		/// the connection will be aborted.
		/// </summary>
		/// <example><code source="..\Examples\ValidateCertificate.cs" lang="cs" /></example>
		public event FtpSslValidation ValidateCertificate {
			add {
				m_sslvalidate += value;
			}
			remove {
				m_sslvalidate -= value;
			}
		}


		private string m_systemType = "UNKNOWN";
		/// <summary>
		/// Gets the type of system/server that we're
		/// connected to.
		/// </summary>
		public string SystemType {
			get {
				return m_systemType;
			}
		}

		private string m_connectionType = "Default";
		/// <summary> Gets the connection type </summary>
		public string ConnectionType {
			get { return m_connectionType; }
			protected set { m_connectionType = value; }
		}

		private int m_transferChunkSize = 65536;
		/// <summary>
		/// Gets or sets the number of bytes transferred in a single chunk (a single FTP command).
		/// Used by <see cref="o:UploadFile"/>/<see cref="o:UploadFileAsync"/> and <see cref="o:DownloadFile"/>/<see cref="o:DownloadFileAsync"/>
		/// to transfer large files in multiple chunks.
		/// </summary>
		public int TransferChunkSize {
			get {
				return m_transferChunkSize;
			}
			set {
				m_transferChunkSize = value;
			}
		}

		private FtpDataType CurrentDataType;

		private FtpParser m_parser = FtpParser.Auto;
		/// <summary>
		/// File listing parser to be used. 
		/// Automatically calculated based on the type of the server, unless changed.
		/// </summary>
		public FtpParser ListingParser {
			get { return m_parser; }
			set {
				m_parser = value;

				// configure parser
				m_listParser.parser = value;
				m_listParser.parserConfirmed = false;
			}
		}

		private CultureInfo m_parserCulture = CultureInfo.InvariantCulture;
		/// <summary>
		/// Culture used to parse file listings
		/// </summary>
		public CultureInfo ListingCulture {
			get { return m_parserCulture; }
			set {
				m_parserCulture = value;

				// configure parser
				m_listParser.parserCulture = value;
			}
		}

		private double m_timeDiff = 0;
		/// <summary>
		/// Time difference between server and client, in hours.
		/// If the server is located in New York and you are in London then the time difference is -5 hours.
		/// </summary>
		public double TimeOffset {
			get { return m_timeDiff; }
			set {
				m_timeDiff = value;

				// configure parser
				int hours = (int)Math.Floor(m_timeDiff);
				int mins = (int)Math.Floor((m_timeDiff - Math.Floor(m_timeDiff)) * 60);
				m_listParser.timeOffset = new TimeSpan(hours, mins, 0);
				m_listParser.hasTimeOffset = m_timeDiff != 0;
			}
		}

		private int m_retryAttempts = 3;
		/// <summary>
		/// Gets or sets the retry attempts allowed when a verification failure occurs during download or upload.
		/// This value must be set to 1 or more.
		/// </summary>
		public int RetryAttempts {
			get { return m_retryAttempts; }
			set { m_retryAttempts = value > 0 ? value : 1; }
		}
		
		uint m_uploadRateLimit = 0;

		/// <summary>
		/// Rate limit for uploads in kbyte/s. Set this to 0 for unlimited speed.
		/// Honored by high-level API such as Upload(), Download(), UploadFile(), DownloadFile()..
		/// </summary>
		public uint UploadRateLimit {
			get { return m_uploadRateLimit; }
			set { m_uploadRateLimit = value; }
		}

		uint m_downloadRateLimit = 0;

		/// <summary>
		/// Rate limit for downloads in kbytes/s. Set this to 0 for unlimited speed.
		/// Honored by high-level API such as Upload(), Download(), UploadFile(), DownloadFile()..
		/// </summary>
		public uint DownloadRateLimit {
			get { return m_downloadRateLimit; }
			set { m_downloadRateLimit = value; }
		}
		// ADD PROPERTIES THAT NEED TO BE CLONED INTO
		// FtpClient.CloneConnection()

		#endregion

		#region Constructor / Destructor

		/// <summary>
		/// Creates a new instance of an FTP Client.
		/// </summary>
		public FtpClient() {
			m_listParser = new FtpListParser(this);
		}

		/// <summary>
		/// Creates a new instance of an FTP Client, with the given host.
		/// </summary>
		public FtpClient(string host) {
			Host = host;
			m_listParser = new FtpListParser(this);
		}

		/// <summary>
		/// Creates a new instance of an FTP Client, with the given host and credentials.
		/// </summary>
		public FtpClient(string host, NetworkCredential credentials) {
			Host = host;
			Credentials = credentials;
			m_listParser = new FtpListParser(this);
		}

		/// <summary>
		/// Creates a new instance of an FTP Client, with the given host, username and password.
		/// </summary>
		public FtpClient(string host, string user, string pass) {
			Host = host;
			Credentials = new NetworkCredential(user, pass);
			m_listParser = new FtpListParser(this);
		}

		/// <summary>
		/// Creates a new instance of this class. Useful in FTP proxy classes.
		/// </summary>
		/// <returns></returns>
		protected virtual FtpClient Create() {
			return new FtpClient();
		}

		/// <summary>
		/// Disconnects from the server, releases resources held by this
		/// object.
		/// </summary>
		public void Dispose() {
			lock (m_lock) {
				if (IsDisposed)
					return;

				FtpTrace.WriteStatus(FtpTraceLevel.Verbose, "Disposing FtpClient object...");

				try {
					if (IsConnected) {
						Disconnect();
					}
				} catch (Exception ex) {
					FtpTrace.WriteLine(FtpTraceLevel.Warn, "FtpClient.Dispose(): Caught and discarded an exception while disconnecting from host: " + ex.ToString());
				}

				if (m_stream != null) {
					try {
						m_stream.Dispose();
					} catch (Exception ex) {
						FtpTrace.WriteLine(FtpTraceLevel.Warn, "FtpClient.Dispose(): Caught and discarded an exception while disposing FtpStream object: " + ex.ToString());
					} finally {
						m_stream = null;
					}
				}

				m_credentials = null;
				m_textEncoding = null;
				m_host = null;
				m_asyncmethods.Clear();
				IsDisposed = true;
				GC.SuppressFinalize(this);
			}
		}

		/// <summary>
		/// Finalizer
		/// </summary>
		~FtpClient() {
			Dispose();
		}

		#endregion

		#region Clone

		/// <summary>
		/// Clones the control connection for opening multiple data streams
		/// </summary>
		/// <returns>A new control connection with the same property settings as this one</returns>
		/// <example><code source="..\Examples\CloneConnection.cs" lang="cs" /></example>
		protected FtpClient CloneConnection() {
			FtpClient conn = Create();

			conn.m_isClone = true;

			// configure new connection as clone of self
			conn.InternetProtocolVersions = InternetProtocolVersions;
			conn.SocketPollInterval = SocketPollInterval;
			conn.StaleDataCheck = StaleDataCheck;
			conn.EnableThreadSafeDataConnections = EnableThreadSafeDataConnections;
			conn.Encoding = Encoding;
			conn.Host = Host;
			conn.Port = Port;
			conn.Credentials = Credentials;
			conn.MaximumDereferenceCount = MaximumDereferenceCount;
			conn.ClientCertificates = ClientCertificates;
			conn.DataConnectionType = DataConnectionType;
			conn.UngracefullDisconnection = UngracefullDisconnection;
			conn.ConnectTimeout = ConnectTimeout;
			conn.ReadTimeout = ReadTimeout;
			conn.DataConnectionConnectTimeout = DataConnectionConnectTimeout;
			conn.DataConnectionReadTimeout = DataConnectionReadTimeout;
			conn.SocketKeepAlive = SocketKeepAlive;
			conn.Capabilities = Capabilities;
			conn.EncryptionMode = EncryptionMode;
			conn.DataConnectionEncryption = DataConnectionEncryption;
			conn.SslProtocols = SslProtocols;
			conn.TransferChunkSize = TransferChunkSize;
			conn.ListingParser = ListingParser;
			conn.ListingCulture = ListingCulture;
			conn.TimeOffset = TimeOffset;
			conn.RetryAttempts = RetryAttempts;
			conn.UploadRateLimit = UploadRateLimit;
			conn.DownloadRateLimit = DownloadRateLimit;

			// copy props using attributes (slower, not .NET core compatible)
			/*foreach (PropertyInfo prop in GetType().GetProperties()) {
				object[] attributes = prop.GetCustomAttributes(typeof(FtpControlConnectionClone), true);

				if (attributes.Length > 0) {
					prop.SetValue(conn, prop.GetValue(this, null), null);
				}
			}*/

			// always accept certificate no matter what because if code execution ever
			// gets here it means the certificate on the control connection object being
			// cloned was already accepted.
			conn.ValidateCertificate += new FtpSslValidation(
				delegate(FtpClient obj, FtpSslValidationEventArgs e) {
					e.Accept = true;
				});

			return conn;
		}
		
		#endregion

		#region Execute Command

		/// <summary>
		/// Executes a command
		/// </summary>
		/// <param name="command">The command to execute</param>
		/// <returns>The servers reply to the command</returns>
		/// <example><code source="..\Examples\Execute.cs" lang="cs" /></example>
		public FtpReply Execute(string command) {
			FtpReply reply;

			lock (m_lock) {
				if (StaleDataCheck) {
					if (m_stream != null && m_stream.SocketDataAvailable > 0) {
						// Data shouldn't be on the socket, if it is it probably
						// means we've been disconnected. Read and discard
						// whatever is there and close the connection.

						FtpTrace.WriteStatus(FtpTraceLevel.Info, "There is stale data on the socket, maybe our connection timed out or you did not call GetReply(). Re-connecting...");
						if (m_stream.IsConnected && !m_stream.IsEncrypted) {
							byte[] buf = new byte[m_stream.SocketDataAvailable];
							m_stream.RawSocketRead(buf);
							FtpTrace.Write(FtpTraceLevel.Verbose, "The data was: " + Encoding.GetString(buf).TrimEnd('\r', '\n'));
						}

						m_stream.Close();
					}
				}

				if (!IsConnected) {
					if (command == "QUIT") {
						FtpTrace.WriteStatus(FtpTraceLevel.Info, "Not sending QUIT because the connection has already been closed.");
						return new FtpReply() {
							Code = "200",
							Message = "Connection already closed."
						};
					}

					Connect();
				}

				string commandTxt = command.StartsWith("PASS") ? "PASS <omitted>" : command;
				FtpTrace.WriteLine(FtpTraceLevel.Info, "Command:  " + commandTxt);
				
				m_stream.WriteLine(m_textEncoding, command);
				reply = GetReply();
			}

			return reply;
		}

		delegate FtpReply AsyncExecute(string command);

		/// <summary>
		/// Performs execution of the specified command asynchronously
		/// </summary>
		/// <param name="command">The command to execute</param>
		/// <param name="callback">The <see cref="AsyncCallback"/> method</param>
		/// <param name="state">State object</param>
		/// <returns>IAsyncResult</returns>
		/// <example><code source="..\Examples\BeginExecute.cs" lang="cs" /></example>
		public IAsyncResult BeginExecute(string command, AsyncCallback callback, object state) {
			AsyncExecute func;
			IAsyncResult ar;

			ar = (func = new AsyncExecute(Execute)).BeginInvoke(command, callback, state);
			lock (m_asyncmethods) {
				m_asyncmethods.Add(ar, func);
			}

			return ar;
		}

		/// <summary>
		/// Ends an asynchronous command
		/// </summary>
		/// <param name="ar">IAsyncResult returned from BeginExecute</param>
		/// <returns>FtpReply object (never null).</returns>
		/// <example><code source="..\Examples\BeginExecute.cs" lang="cs" /></example>
		public FtpReply EndExecute(IAsyncResult ar) {
			return GetAsyncDelegate<AsyncExecute>(ar).EndInvoke(ar);
		}

#if (CORE || NETFX45)
		/// <summary>
		/// Performs an asynchronous execution of the specified command
		/// </summary>
		/// <param name="command">The command to execute</param>
		/// <returns>The servers reply to the command</returns>
		public async Task<FtpReply> ExecuteAsync(string command) {
			//TODO:  Rewrite as true async method with cancellation support
			return await Task.Factory.FromAsync<string, FtpReply>(
				(c, ac, state) => BeginExecute(c, ac, state),
				ar => EndExecute(ar),
				command, null);
		}
#endif

		#endregion

		#region Get Reply

		/// <summary>
		/// Retrieves a reply from the server. Do not execute this method
		/// unless you are sure that a reply has been sent, i.e., you
		/// executed a command. Doing so will cause the code to hang
		/// indefinitely waiting for a server reply that is never coming.
		/// </summary>
		/// <returns>FtpReply representing the response from the server</returns>
		/// <example><code source="..\Examples\BeginGetReply.cs" lang="cs" /></example>
		public FtpReply GetReply() {
			FtpReply reply = new FtpReply();
			string buf;

			lock (m_lock) {
				if (!IsConnected)
					throw new InvalidOperationException("No connection to the server has been established.");

				m_stream.ReadTimeout = m_readTimeout;
				while ((buf = m_stream.ReadLine(Encoding)) != null) {
					Match m;


					if ((m = Regex.Match(buf, "^(?<code>[0-9]{3}) (?<message>.*)$")).Success) {
						reply.Code = m.Groups["code"].Value;
						reply.Message = m.Groups["message"].Value;
						break;
					}

					reply.InfoMessages += (buf + "\n");
				}

				// log response code + message
				if (reply.Code != null) {
					FtpTrace.WriteLine(FtpTraceLevel.Info, "Response: " + reply.Code + " " + reply.Message);
				}

				// log multiline response messages
				if (reply.InfoMessages != null) {
					reply.InfoMessages = reply.InfoMessages.Trim();
				}
				if (!string.IsNullOrEmpty(reply.InfoMessages)) {
					//FtpTrace.WriteLine(FtpTraceLevel.Verbose, "+---------------------------------------+");
					FtpTrace.WriteLine(FtpTraceLevel.Verbose, reply.InfoMessages.Split('\n').AddPrefix("Response: ", true).Join("\n"));
					//FtpTrace.WriteLine(FtpTraceLevel.Verbose, "-----------------------------------------");
				}
			}

			return reply;
		}

		#endregion

		#region Connect

		private FtpListParser m_listParser;

		/// <summary>
		/// Connect to the server
		/// </summary>
		/// <exception cref="ObjectDisposedException">Thrown if this object has been disposed.</exception>
		/// <example><code source="..\Examples\Connect.cs" lang="cs" /></example>
		public virtual void Connect() {
			FtpReply reply;

			lock (m_lock) {
				if (IsDisposed)
					throw new ObjectDisposedException("This FtpClient object has been disposed. It is no longer accessible.");

				if (m_stream == null) {
					m_stream = new FtpSocketStream();
					m_stream.ValidateCertificate += new FtpSocketStreamSslValidation(FireValidateCertficate);
				} else {
					if (IsConnected) {
						Disconnect();
					}
				}

				if (Host == null) {
					throw new FtpException("No host has been specified");
				}

				if (!IsClone) {
					m_caps = FtpCapability.NONE;
				}

				m_hashAlgorithms = FtpHashAlgorithm.NONE;
				m_stream.ConnectTimeout = m_connectTimeout;
				m_stream.SocketPollInterval = m_socketPollInterval;
				Connect(m_stream);

				m_stream.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, m_keepAlive);

#if !NO_SSL
				if (EncryptionMode == FtpEncryptionMode.Implicit) {
					m_stream.ActivateEncryption(Host, m_clientCerts.Count > 0 ? m_clientCerts : null, m_SslProtocols);
				}
#endif

				Handshake();

#if !NO_SSL
				if (EncryptionMode == FtpEncryptionMode.Explicit) {
					if (!(reply = Execute("AUTH TLS")).Success) {
						throw new FtpSecurityNotAvailableException("AUTH TLS command failed.");
					}
					m_stream.ActivateEncryption(Host, m_clientCerts.Count > 0 ? m_clientCerts : null, m_SslProtocols);
				}
#endif

				if (m_credentials != null) {
					Authenticate();
				}

				if (m_stream.IsEncrypted && DataConnectionEncryption) {
					if (!(reply = Execute("PBSZ 0")).Success)
						throw new FtpCommandException(reply);
					if (!(reply = Execute("PROT P")).Success)
						throw new FtpCommandException(reply);
				}

				// if this is a clone these values should have already been loaded
				// so save some bandwidth and CPU time and skip executing this again.
				if (!IsClone) {
					if ((reply = Execute("FEAT")).Success && reply.InfoMessages != null) {
						GetFeatures(reply);
					}
				}

				// Enable UTF8 if the encoding is ASCII and UTF8 is supported
				if (m_textEncodingAutoUTF && m_textEncoding == Encoding.ASCII && HasFeature(FtpCapability.UTF8)) {
					m_textEncoding = Encoding.UTF8;
				}

				FtpTrace.WriteStatus(FtpTraceLevel.Info, "Text encoding: " + m_textEncoding.ToString());

				if (m_textEncoding == Encoding.UTF8) {
					// If the server supports UTF8 it should already be enabled and this
					// command should not matter however there are conflicting drafts
					// about this so we'll just execute it to be safe. 
					Execute("OPTS UTF8 ON");
				}

				// Get the system type - Needed to auto-detect file listing parser
				if ((reply = Execute("SYST")).Success) {
					m_systemType = reply.Message;
				}

				// Create the parser even if the auto-OS detection failed
				m_listParser.Init(m_systemType);

			}
		}

		/// <summary>
		/// Connect to the FTP server. Overwritten in proxy classes.
		/// </summary>
		/// <param name="stream"></param>
		protected virtual void Connect(FtpSocketStream stream) {
			stream.Connect(Host, Port, InternetProtocolVersions);
		}

		/// <summary>
		/// Connect to the FTP server. Overwritten in proxy classes.
		/// </summary>
		protected virtual void Connect(FtpSocketStream stream, string host, int port, FtpIpVersion ipVersions) {
			stream.Connect(host, port, ipVersions);
		}

		/// <summary>
		/// Called during Connect(). Typically extended by FTP proxies.
		/// </summary>
		protected virtual void Handshake() {
			FtpReply reply;
			if (!(reply = GetReply()).Success) {
				if (reply.Code == null) {
					throw new IOException("The connection was terminated before a greeting could be read.");
				} else {
					throw new FtpCommandException(reply);
				}
			}
		}

		/// <summary>
		/// Populates the capabilities flags based on capabilities
		/// supported by this server. This method is overridable
		/// so that new features can be supported
		/// </summary>
		/// <param name="reply">The reply object from the FEAT command. The InfoMessages property will
		/// contain a list of the features the server supported delimited by a new line '\n' character.</param>
		protected virtual void GetFeatures(FtpReply reply) {
			foreach (string feat in reply.InfoMessages.Split('\n')) {
				if (feat.ToUpper().Trim().StartsWith("MLST") || feat.ToUpper().Trim().StartsWith("MLSD"))
					m_caps |= FtpCapability.MLSD;
				else if (feat.ToUpper().Trim().StartsWith("MDTM"))
					m_caps |= FtpCapability.MDTM;
				else if (feat.ToUpper().Trim().StartsWith("REST STREAM"))
					m_caps |= FtpCapability.REST;
				else if (feat.ToUpper().Trim().StartsWith("SIZE"))
					m_caps |= FtpCapability.SIZE;
				else if (feat.ToUpper().Trim().StartsWith("UTF8"))
					m_caps |= FtpCapability.UTF8;
				else if (feat.ToUpper().Trim().StartsWith("PRET"))
					m_caps |= FtpCapability.PRET;
				else if (feat.ToUpper().Trim().StartsWith("MFMT"))
					m_caps |= FtpCapability.MFMT;
				else if (feat.ToUpper().Trim().StartsWith("MFCT"))
					m_caps |= FtpCapability.MFCT;
				else if (feat.ToUpper().Trim().StartsWith("MFF"))
					m_caps |= FtpCapability.MFF;
				else if (feat.ToUpper().Trim().StartsWith("MD5"))
					m_caps |= FtpCapability.MD5;
				else if (feat.ToUpper().Trim().StartsWith("XMD5"))
					m_caps |= FtpCapability.XMD5;
				else if (feat.ToUpper().Trim().StartsWith("XCRC"))
					m_caps |= FtpCapability.XCRC;
				else if (feat.ToUpper().Trim().StartsWith("XSHA1"))
					m_caps |= FtpCapability.XSHA1;
				else if (feat.ToUpper().Trim().StartsWith("XSHA256"))
					m_caps |= FtpCapability.XSHA256;
				else if (feat.ToUpper().Trim().StartsWith("XSHA512"))
					m_caps |= FtpCapability.XSHA512;
				else if (feat.ToUpper().Trim().StartsWith("HASH")) {
					Match m;

					m_caps |= FtpCapability.HASH;

					if ((m = Regex.Match(feat.ToUpper().Trim(), @"^HASH\s+(?<types>.*)$")).Success) {
						foreach (string type in m.Groups["types"].Value.Split(';')) {
							switch (type.ToUpper().Trim()) {
								case "SHA-1":
								case "SHA-1*":
									m_hashAlgorithms |= FtpHashAlgorithm.SHA1;
									break;
								case "SHA-256":
								case "SHA-256*":
									m_hashAlgorithms |= FtpHashAlgorithm.SHA256;
									break;
								case "SHA-512":
								case "SHA-512*":
									m_hashAlgorithms |= FtpHashAlgorithm.SHA512;
									break;
								case "MD5":
								case "MD5*":
									m_hashAlgorithms |= FtpHashAlgorithm.MD5;
									break;
								case "CRC":
								case "CRC*":
									m_hashAlgorithms |= FtpHashAlgorithm.CRC;
									break;
							}
						}
					}
				}
			}
		}

		delegate void AsyncConnect();

		/// <summary>
		/// Initiates a connection to the server
		/// </summary>
		/// <param name="callback">AsyncCallback method</param>
		/// <param name="state">State object</param>
		/// <returns>IAsyncResult</returns>
		/// <example><code source="..\Examples\BeginConnect.cs" lang="cs" /></example>
		public IAsyncResult BeginConnect(AsyncCallback callback, object state) {
			AsyncConnect func;
			IAsyncResult ar;

			ar = (func = new AsyncConnect(Connect)).BeginInvoke(callback, state);

			lock (m_asyncmethods) {
				m_asyncmethods.Add(ar, func);
			}

			return ar;
		}

		/// <summary>
		/// Ends an asynchronous connection attempt to the server from <see cref="BeginConnect"/>
		/// </summary>
		/// <param name="ar"><see cref="IAsyncResult"/> returned from <see cref="BeginConnect"/></param>
		/// <example><code source="..\Examples\BeginConnect.cs" lang="cs" /></example>
		public void EndConnect(IAsyncResult ar) {
			GetAsyncDelegate<AsyncConnect>(ar).EndInvoke(ar);
		}

#if (CORE || NETFX45)
		/// <summary>
		/// Connects to the server asynchronously
		/// </summary>
		public async Task ConnectAsync() {
			//TODO:  Rewrite as true async method with cancellation support
			await Task.Factory.FromAsync(
				(c, s) => BeginConnect(c, s),
				ar => EndConnect(ar),
				null);
		}
#endif
		
		#endregion

		#region Login

		/// <summary>
		/// Performs a login on the server. This method is overridable so
		/// that the login procedure can be changed to support, for example,
		/// a FTP proxy.
		/// </summary>
		protected virtual void Authenticate() {
			Authenticate(Credentials.UserName, Credentials.Password);
		}

		/// <summary>
		/// Performs a login on the server. This method is overridable so
		/// that the login procedure can be changed to support, for example,
		/// a FTP proxy.
		/// </summary>
		protected virtual void Authenticate(string userName, string password) {
			FtpReply reply;

			if (!(reply = Execute("USER " + userName)).Success)
				throw new FtpCommandException(reply);

			if (reply.Type == FtpResponseType.PositiveIntermediate
				&& !(reply = Execute("PASS " + password)).Success)
				throw new FtpCommandException(reply);
		}

		#endregion

		#region Disconnect

		/// <summary>
		/// Disconnects from the server
		/// </summary>
		public virtual void Disconnect() {
			lock (m_lock) {
				if (m_stream != null && m_stream.IsConnected) {
					try {
						if (!UngracefullDisconnection) {
							Execute("QUIT");
						}
					} catch (SocketException sockex) {
						FtpTrace.WriteStatus(FtpTraceLevel.Warn, "FtpClient.Disconnect(): SocketException caught and discarded while closing control connection: " + sockex.ToString());
					} catch (IOException ioex) {
						FtpTrace.WriteStatus(FtpTraceLevel.Warn, "FtpClient.Disconnect(): IOException caught and discarded while closing control connection: " + ioex.ToString());
					} catch (FtpCommandException cmdex) {
						FtpTrace.WriteStatus(FtpTraceLevel.Warn, "FtpClient.Disconnect(): FtpCommandException caught and discarded while closing control connection: " + cmdex.ToString());
					} catch (FtpException ftpex) {
						FtpTrace.WriteStatus(FtpTraceLevel.Warn, "FtpClient.Disconnect(): FtpException caught and discarded while closing control connection: " + ftpex.ToString());
					} finally {
						m_stream.Close();
					}
				}
			}
		}

		delegate void AsyncDisconnect();

		/// <summary>
		/// Initiates a disconnection on the server
		/// </summary>
		/// <param name="callback"><see cref="AsyncCallback"/> method</param>
		/// <param name="state">State object</param>
		/// <returns>IAsyncResult</returns>
		/// <example><code source="..\Examples\BeginDisconnect.cs" lang="cs" /></example>
		public IAsyncResult BeginDisconnect(AsyncCallback callback, object state) {
			IAsyncResult ar;
			AsyncDisconnect func;

			ar = (func = new AsyncDisconnect(Disconnect)).BeginInvoke(callback, state);
			lock (m_asyncmethods) {
				m_asyncmethods.Add(ar, func);
			}

			return ar;
		}

		/// <summary>
		/// Ends a call to <see cref="BeginDisconnect"/>
		/// </summary>
		/// <param name="ar"><see cref="IAsyncResult"/> returned from <see cref="BeginDisconnect"/></param>
		/// <example><code source="..\Examples\BeginConnect.cs" lang="cs" /></example>
		public void EndDisconnect(IAsyncResult ar) {
			GetAsyncDelegate<AsyncDisconnect>(ar).EndInvoke(ar);
		}

#if (CORE || NETFX45)
		/// <summary>
		/// Disconnects from the server asynchronously
		/// </summary>
		public async Task DisconnectAsync() {
			//TODO:  Rewrite as true async method with cancellation support
			await Task.Factory.FromAsync(
				(c, s) => BeginDisconnect(c, s),
				ar => EndDisconnect(ar),
				null);
		}
#endif

		#endregion

		#region FTPS

		/// <summary>
		/// Catches the socket stream ssl validation event and fires the event handlers
		/// attached to this object for validating SSL certificates
		/// </summary>
		/// <param name="stream">The stream that fired the event</param>
		/// <param name="e">The event args used to validate the certificate</param>
		void FireValidateCertficate(FtpSocketStream stream, FtpSslValidationEventArgs e) {
			OnValidateCertficate(e);
		}

		/// <summary>
		/// Fires the SSL validation event
		/// </summary>
		/// <param name="e">Event Args</param>
		void OnValidateCertficate(FtpSslValidationEventArgs e) {
			FtpSslValidation evt;

			evt = m_sslvalidate;
			if (evt != null)
				evt(this, e);
		}

		#endregion

		#region Utils

		/// <summary>
		/// Performs a bitwise and to check if the specified
		/// flag is set on the <see cref="Capabilities"/>  property.
		/// </summary>
		/// <param name="cap">The <see cref="FtpCapability"/> to check for</param>
		/// <returns>True if the feature was found, false otherwise</returns>
		public bool HasFeature(FtpCapability cap) {
			return ((this.Capabilities & cap) == cap);
		}

		/// <summary>
		/// Retrieves the delegate for the specified IAsyncResult and removes
		/// it from the m_asyncmethods collection if the operation is successful
		/// </summary>
		/// <typeparam name="T">Type of delegate to retrieve</typeparam>
		/// <param name="ar">The IAsyncResult to retrieve the delegate for</param>
		/// <returns>The delegate that generated the specified IAsyncResult</returns>
		protected T GetAsyncDelegate<T>(IAsyncResult ar) {
			T func;

			lock (m_asyncmethods) {
				if (m_isDisposed) {
					throw new ObjectDisposedException("This connection object has already been disposed.");
				}

				if (!m_asyncmethods.ContainsKey(ar))
					throw new InvalidOperationException("The specified IAsyncResult could not be located.");

				if (!(m_asyncmethods[ar] is T)) {
#if CORE
					throw new InvalidCastException("The AsyncResult cannot be matched to the specified delegate. ");
#else
					StackTrace st = new StackTrace(1);

					throw new InvalidCastException("The AsyncResult cannot be matched to the specified delegate. " +
						("Are you sure you meant to call " + st.GetFrame(0).GetMethod().Name + " and not another method?")
					);
#endif
				}

				func = (T)m_asyncmethods[ar];
				m_asyncmethods.Remove(ar);
			}

			return func;
		}

		/// <summary>
		/// Ensure a relative path is absolute by appending the working dir
		/// </summary>
		private string GetAbsolutePath(string path) {
			if (path == null || path.Trim().Length == 0) {

				// if path not given, then use working dir
				string pwd = GetWorkingDirectory();
				if (pwd != null && pwd.Trim().Length > 0)
					path = pwd;
				else
					path = "./";

			} else if (!path.StartsWith("/")) {

				// if relative path given then add working dir to calc full path
				string pwd = GetWorkingDirectory();
				if (pwd != null && pwd.Trim().Length > 0) {
					if (path.StartsWith("./"))
						path = path.Remove(0, 2);
					path = (pwd + "/" + path).GetFtpPath();
				}
			}
			return path;
		}

		private static string DecodeUrl(string url) {
#if CORE
			return WebUtility.UrlDecode(url);
#else
			return HttpUtility.UrlDecode(url);
#endif
		}

		private static byte[] ReadToEnd(Stream stream, long maxLength, int chunkLen) {
			int read = 1;
			byte[] buffer = new byte[chunkLen];
			using (var mem = new MemoryStream()) {
				do {
					long length = maxLength == 0 ? buffer.Length : Math.Min(maxLength - (int)mem.Length, buffer.Length);
					read = stream.Read(buffer, 0, (int)length);
					mem.Write(buffer, 0, read);
					if (maxLength > 0 && mem.Length == maxLength) break;
				} while (read > 0);

				return mem.ToArray();
			}
		}

		/// <summary>
		/// Disables UTF8 support and changes the Encoding property
		/// back to ASCII. If the server returns an error when trying
		/// to turn UTF8 off a FtpCommandException will be thrown.
		/// </summary>
		public void DisableUTF8() {
			FtpReply reply;

			lock (m_lock) {
				if (!(reply = Execute("OPTS UTF8 OFF")).Success)
					throw new FtpCommandException(reply);

				m_textEncoding = Encoding.ASCII;
				m_textEncodingAutoUTF = false;
			}
		}

		private void ReadStaleData() {
			if (m_stream != null && m_stream.SocketDataAvailable > 0) {
				if (m_stream.IsConnected && !m_stream.IsEncrypted) {
					byte[] buf = new byte[m_stream.SocketDataAvailable];
					m_stream.RawSocketRead(buf);
					FtpTrace.Write(FtpTraceLevel.Verbose, "The data was: ");
					FtpTrace.WriteLine(FtpTraceLevel.Verbose, Encoding.GetString(buf).TrimEnd('\r', '\n'));
				}
			}
		}

		private bool IsProxy() {
			return (this is FtpClientProxy);
		}

		private static string[] fileNotFoundStrings = new string[] { "can't check for file existence", "does not exist", "failed to open file", "not found", "no such file", "cannot find the file", "cannot find", "could not get file", "not a regular file", "file unavailable", "file is unavailable", "file not unavailable", "file is not available" };
		private bool IsKnownError(string reply, string[] strings) {
			reply = reply.ToLower();
			foreach (string msg in strings) {
				if (reply.Contains(msg)) {
					return true;
				}
			}
			return false;
		}

		#endregion

		#region Static API

		/// <summary>
		/// Calculate the CHMOD integer value given a set of permissions.
		/// </summary>
		public static int CalcChmod(FtpPermission owner, FtpPermission group, FtpPermission other) {

			int chmod = 0;

			if (HasPermission(owner, FtpPermission.Read)) {
				chmod += 400;
			}
			if (HasPermission(owner, FtpPermission.Write)) {
				chmod += 200;
			}
			if (HasPermission(owner, FtpPermission.Execute)) {
				chmod += 100;
			}

			if (HasPermission(group, FtpPermission.Read)) {
				chmod += 40;
			}
			if (HasPermission(group, FtpPermission.Write)) {
				chmod += 20;
			}
			if (HasPermission(group, FtpPermission.Execute)) {
				chmod += 10;
			}

			if (HasPermission(other, FtpPermission.Read)) {
				chmod += 4;
			}
			if (HasPermission(other, FtpPermission.Write)) {
				chmod += 2;
			}
			if (HasPermission(other, FtpPermission.Execute)) {
				chmod += 1;
			}

			return chmod;
		}

		private static bool HasPermission(FtpPermission owner, FtpPermission flag) {
			return (owner & flag) == flag;
		}

		//TODO:  Create async versions of static methods

		/// <summary>
		/// Connects to the specified URI. If the path specified by the URI ends with a
		/// / then the working directory is changed to the path specified.
		/// </summary>
		/// <param name="uri">The URI to parse</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <returns>FtpClient object</returns>
		public static FtpClient Connect(Uri uri, bool checkcertificate) {
			FtpClient cl = new FtpClient();

			if (uri == null)
				throw new ArgumentException("Invalid URI object");

			switch (uri.Scheme.ToLower()) {
				case "ftp":
				case "ftps":
					break;
				default:
					throw new UriFormatException("The specified URI scheme is not supported. Please use ftp:// or ftps://");
			}

			cl.Host = uri.Host;
			cl.Port = uri.Port;

			if (uri.UserInfo != null && uri.UserInfo.Length > 0) {
				if (uri.UserInfo.Contains(":")) {
					string[] parts = uri.UserInfo.Split(':');

					if (parts.Length != 2)
						throw new UriFormatException("The user info portion of the URI contains more than 1 colon. The username and password portion of the URI should be URL encoded.");

					cl.Credentials = new NetworkCredential(DecodeUrl(parts[0]), DecodeUrl(parts[1]));
				} else
					cl.Credentials = new NetworkCredential(DecodeUrl(uri.UserInfo), "");
			} else {
				// if no credentials were supplied just make up
				// some for anonymous authentication.
				cl.Credentials = new NetworkCredential("ftp", "ftp");
			}

			cl.ValidateCertificate += new FtpSslValidation(delegate(FtpClient control, FtpSslValidationEventArgs e) {
				if (e.PolicyErrors != System.Net.Security.SslPolicyErrors.None && checkcertificate)
					e.Accept = false;
				else
					e.Accept = true;
			});

			cl.Connect();

			if (uri.PathAndQuery != null && uri.PathAndQuery.EndsWith("/"))
				cl.SetWorkingDirectory(uri.PathAndQuery);

			return cl;
		}

		/// <summary>
		/// Connects to the specified URI. If the path specified by the URI ends with a
		/// / then the working directory is changed to the path specified.
		/// </summary>
		/// <param name="uri">The URI to parse</param>
		/// <returns>FtpClient object</returns>
		public static FtpClient Connect(Uri uri) {
			return Connect(uri, true);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <param name="datatype">ASCII/Binary mode</param>
		/// <param name="restart">Restart location</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenReadURI.cs" lang="cs" /></example>
		public static Stream OpenRead(Uri uri, bool checkcertificate, FtpDataType datatype, long restart) {
			FtpClient cl = null;

			CheckURI(uri);

			cl = Connect(uri, checkcertificate);
			cl.EnableThreadSafeDataConnections = false;

			return cl.OpenRead(uri.PathAndQuery, datatype, restart);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <param name="datatype">ASCII/Binary mode</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenReadURI.cs" lang="cs" /></example>
		public static Stream OpenRead(Uri uri, bool checkcertificate, FtpDataType datatype) {
			return OpenRead(uri, checkcertificate, datatype, 0);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenReadURI.cs" lang="cs" /></example>
		public static Stream OpenRead(Uri uri, bool checkcertificate) {
			return OpenRead(uri, checkcertificate, FtpDataType.Binary, 0);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenReadURI.cs" lang="cs" /></example>
		public static Stream OpenRead(Uri uri) {
			return OpenRead(uri, true, FtpDataType.Binary, 0);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <param name="datatype">ASCII/Binary mode</param> 
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenWriteURI.cs" lang="cs" /></example>
		public static Stream OpenWrite(Uri uri, bool checkcertificate, FtpDataType datatype) {
			FtpClient cl = null;

			CheckURI(uri);

			cl = Connect(uri, checkcertificate);
			cl.EnableThreadSafeDataConnections = false;

			return cl.OpenWrite(uri.PathAndQuery, datatype);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenWriteURI.cs" lang="cs" /></example>
		public static Stream OpenWrite(Uri uri, bool checkcertificate) {
			return OpenWrite(uri, checkcertificate, FtpDataType.Binary);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenWriteURI.cs" lang="cs" /></example>
		public static Stream OpenWrite(Uri uri) {
			return OpenWrite(uri, true, FtpDataType.Binary);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <param name="datatype">ASCII/Binary mode</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenAppendURI.cs" lang="cs" /></example>
		public static Stream OpenAppend(Uri uri, bool checkcertificate, FtpDataType datatype) {
			FtpClient cl = null;

			CheckURI(uri);

			cl = Connect(uri, checkcertificate);
			cl.EnableThreadSafeDataConnections = false;

			return cl.OpenAppend(uri.PathAndQuery, datatype);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <param name="checkcertificate">Indicates if a ssl certificate should be validated when using FTPS schemes</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenAppendURI.cs" lang="cs" /></example>
		public static Stream OpenAppend(Uri uri, bool checkcertificate) {
			return OpenAppend(uri, checkcertificate, FtpDataType.Binary);
		}

		/// <summary>
		/// Opens a stream to the file specified by the URI
		/// </summary>
		/// <param name="uri">FTP/FTPS URI pointing at a file</param>
		/// <returns>Stream object</returns>
		/// <example><code source="..\Examples\OpenAppendURI.cs" lang="cs" /></example>
		public static Stream OpenAppend(Uri uri) {
			return OpenAppend(uri, true, FtpDataType.Binary);
		}

		private static void CheckURI(Uri uri) {

			if (string.IsNullOrEmpty(uri.PathAndQuery)) {
				throw new UriFormatException("The supplied URI does not contain a valid path.");
			}

			if (uri.PathAndQuery.EndsWith("/")) {
				throw new UriFormatException("The supplied URI points at a directory.");
			}
		}

		/// <summary>
		/// Calculate you public internet IP using the ipify service. Returns null if cannot be calculated.
		/// </summary>
		/// <returns>Public IP Address</returns>
		public static string GetPublicIP() {
#if NETFX
			try {
				var request = WebRequest.Create("https://api.ipify.org/");
				request.Method = "GET";

				using (var response = request.GetResponse()) {
					using (var stream = new StreamReader(response.GetResponseStream())) {
						return stream.ReadToEnd();
					}
				}
			} catch (Exception) { }
#endif
			return null;
		}

		#endregion

	}
}