﻿/* Copyright 2010-2012 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using MongoDB.Driver.Internal;

namespace MongoDB.Driver
{
    /// <summary>
    /// Represents an instance of a MongoDB server host (in the case of a replica set a MongoServer uses multiple MongoServerInstances).
    /// </summary>
    internal enum MongoServerInstanceType
    {
        /// <summary>
        /// The server instance type is unknown.  This is the default.
        /// </summary>
        Unknown,
        /// <summary>
        /// The server is a standalone instance.
        /// </summary>
        StandAlone,
        /// <summary>
        /// The server is a replica set member.
        /// </summary>
        ReplicaSetMember,
        /// <summary>
        /// The server is a shard router (mongos).
        /// </summary>
        ShardRouter
    }

    /// <summary>
    /// Represents an instance of a MongoDB server host.
    /// </summary>
    public sealed class MongoServerInstance
    {
        // private static fields
        private static int __nextSequentialId;

        // public events
        /// <summary>
        /// Occurs when the value of the State property changes.
        /// </summary>
        public event EventHandler StateChanged;

        //internal events
        internal event EventHandler AveragePingTimeChanged;

        // private fields
        private readonly object _stateLock = new object();
        private readonly MongoServer _server;
        private readonly MongoConnectionPool _connectionPool;
        private readonly PingTimeAggregator _pingTimeAggregator;
        private MongoServerAddress _address;
        private Exception _connectException;
        private bool _inStateVerification;
        private ServerInformation _stateInfo;
        private IPEndPoint _ipEndPoint;
        private int _sequentialId;
        private MongoServerState _state;
        private Timer _stateVerificationTimer;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="MongoServerInstance"/> class.
        /// </summary>
        /// <param name="server">The server.</param>
        /// <param name="address">The address.</param>
        internal MongoServerInstance(MongoServer server, MongoServerAddress address)
        {
            _server = server;
            _address = address;
            _sequentialId = Interlocked.Increment(ref __nextSequentialId);
            _state = MongoServerState.Disconnected;
            _stateInfo = new ServerInformation
            {
                MaxDocumentSize = MongoDefaults.MaxDocumentSize,
                MaxMessageLength = MongoDefaults.MaxMessageLength,
                InstanceType = MongoServerInstanceType.Unknown
            };
            _connectionPool = new MongoConnectionPool(this);
            _pingTimeAggregator = new PingTimeAggregator(5);
            // Console.WriteLine("MongoServerInstance[{0}]: {1}", sequentialId, address);
        }

        // internal properties
        internal TimeSpan AveragePingTime
        {
            get { return _pingTimeAggregator.Average; }
        }

        /// <summary>
        /// Gets the replica set information.
        /// </summary>
        internal ReplicaSetInformation ReplicaSetInformation
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).ReplicaSetInformation;
            }
        }

        /// <summary>
        /// Gets the instance type.
        /// </summary>
        internal MongoServerInstanceType InstanceType
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).InstanceType;
            }
        }

        // public properties
        /// <summary>
        /// Gets the address of this server instance.
        /// </summary>
        public MongoServerAddress Address
        {
            get
            {
                return Interlocked.CompareExchange(ref _address, null, null);
            }
            internal set
            {
                Interlocked.Exchange(ref _address, value);
            }
        }

        /// <summary>
        /// Gets the version of this server instance.
        /// </summary>
        public MongoServerBuildInfo BuildInfo
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).BuildInfo;
            }
        }

        /// <summary>
        /// Gets the exception thrown the last time Connect was called (null if Connect did not throw an exception).
        /// </summary>
        public Exception ConnectException
        {
            get
            {
                return Interlocked.CompareExchange(ref _connectException, null, null);
            }
        }

        /// <summary>
        /// Gets the connection pool for this server instance.
        /// </summary>
        public MongoConnectionPool ConnectionPool
        {
            get { return _connectionPool; }
        }

        /// <summary>
        /// Gets whether this server instance is an arbiter instance.
        /// </summary>
        public bool IsArbiter
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).IsArbiter;
            }
        }

        /// <summary>
        /// Gets the result of the most recent ismaster command sent to this server instance.
        /// </summary>
        public IsMasterResult IsMasterResult
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).IsMasterResult;
            }
        }

        /// <summary>
        /// Gets whether this server instance is a passive instance.
        /// </summary>
        public bool IsPassive
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).IsPassive;
            }
        }

        /// <summary>
        /// Gets whether this server instance is a primary.
        /// </summary>
        public bool IsPrimary
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).IsPrimary;
            }
        }

        /// <summary>
        /// Gets whether this server instance is a secondary.
        /// </summary>
        public bool IsSecondary
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).IsSecondary;
            }
        }

        /// <summary>
        /// Gets the max document size for this server instance.
        /// </summary>
        public int MaxDocumentSize
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).MaxDocumentSize;
            }
        }

        /// <summary>
        /// Gets the max message length for this server instance.
        /// </summary>
        public int MaxMessageLength
        {
            get
            {
                return Interlocked.CompareExchange(ref _stateInfo, null, null).MaxMessageLength;
            }
        }

        /// <summary>
        /// Gets the unique sequential Id for this server instance.
        /// </summary>
        public int SequentialId
        {
            get { return _sequentialId; }
        }

        /// <summary>
        /// Gets the server for this server instance.
        /// </summary>
        public MongoServer Server
        {
            get { return _server; }
        }

        /// <summary>
        /// Gets the state of this server instance.
        /// </summary>
        public MongoServerState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        // public methods
        /// <summary>
        /// Gets the IP end point of this server instance.
        /// </summary>
        /// <returns>The IP end point of this server instance.</returns>
        public IPEndPoint GetIPEndPoint()
        {
            // use a lock free algorithm because DNS lookups are rare and concurrent lookups are tolerable
            // the intermediate variable is important to avoid race conditions
            var ipEndPoint = Interlocked.CompareExchange(ref _ipEndPoint, null, null);
            if (ipEndPoint == null)
            {
                ipEndPoint = _address.ToIPEndPoint(_server.Settings.AddressFamily);
                Interlocked.CompareExchange(ref _ipEndPoint, _ipEndPoint, null);
            }
            return ipEndPoint;
        }

        /// <summary>
        /// Checks whether the server is alive (throws an exception if not).
        /// </summary>
        public void Ping()
        {
            // use a new connection instead of one from the connection pool
            var connection = new MongoConnection(this);
            try
            {
                Ping(connection);
            }
            finally
            {
                connection.Close();
            }
        }

        /// <summary>
        /// Verifies the state of the server instance.
        /// </summary>
        public void VerifyState()
        {
            // use a new connection instead of one from the connection pool
            var connection = new MongoConnection(this);
            try
            {
                try
                {
                    Ping(connection);
                    LookupServerInformation(connection);
                }
                catch
                {
                    // ignore exceptions (if any occured state will already be set to Disconnected)
                    // Console.WriteLine("MongoServerInstance[{0}]: VerifyState failed: {1}.", sequentialId, ex.Message);
                }
            }
            finally
            {
                connection.Close();
            }
        }

        // internal methods
        /// <summary>
        /// Acquires the connection.
        /// </summary>
        /// <param name="database">The database.</param>
        /// <returns>A MongoConnection.</returns>
        internal MongoConnection AcquireConnection(MongoDatabase database)
        {
            MongoConnection connection;
            lock (_stateLock)
            {
                if (_state != MongoServerState.Connected)
                {
                    var message = string.Format("Server instance {0} is no longer connected.", _address);
                    throw new InvalidOperationException(message);
                }
            }

            connection = _connectionPool.AcquireConnection(database);

            // check authentication outside the lock because it might involve a round trip to the server
            try
            {
                connection.CheckAuthentication(database); // will authenticate if necessary
            }
            catch (MongoAuthenticationException)
            {
                // don't let the connection go to waste just because authentication failed
                _connectionPool.ReleaseConnection(connection);
                throw;
            }

            return connection;
        }

        /// <summary>
        /// Connects this instance.
        /// </summary>
        internal void Connect()
        {
            // Console.WriteLine("MongoServerInstance[{0}]: Connect() called.", sequentialId);
            lock (_stateLock)
            {
                if (_state == MongoServerState.Connecting || _state == MongoServerState.Connected)
                {
                    return;
                }
            }

            // There is a possibility that multiple threads enter into this area.  While it is highly unlikely,
            // it is not a problem even if they do as there are no adverse affects.

            Interlocked.Exchange(ref _connectException, null);

            SetState(MongoServerState.Connecting);

            try
            {
                var connection = _connectionPool.AcquireConnection(null);
                try
                {
                    Ping(connection);
                    LookupServerInformation(connection);
                }
                finally
                {
                    _connectionPool.ReleaseConnection(connection);
                }
                SetState(MongoServerState.Connected);
            }
            catch (Exception ex)
            {
                _connectionPool.Clear();
                Interlocked.Exchange(ref _connectException, ex);
                SetState(MongoServerState.Disconnected);
                throw;
            }
            finally
            {
                lock (_stateLock)
                {
                    if (_stateVerificationTimer == null)
                    {
                        _stateVerificationTimer = new Timer(o => StateVerificationTimerCallback(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                    }
                }
            }
        }

        /// <summary>
        /// Disconnects this instance.
        /// </summary>
        internal void Disconnect()
        {
            // Console.WriteLine("MongoServerInstance[{0}]: Disconnect called.", sequentialId);
            lock (_stateLock)
            {
                if (_state == MongoServerState.Disconnecting)
                {
                    throw new MongoInternalException("Disconnect called while disconnecting.");
                }

                if (_stateVerificationTimer != null)
                {
                    _stateVerificationTimer.Dispose();
                    _stateVerificationTimer = null;
                }

                if (_state == MongoServerState.Disconnected)
                {
                    return;
                }
            }

            try
            {
                SetState(MongoServerState.Disconnecting);
                _connectionPool.Clear();
            }
            finally
            {
                SetState(MongoServerState.Disconnected);
            }
        }

        /// <summary>
        /// Releases the connection.
        /// </summary>
        /// <param name="connection">The connection.</param>
        internal void ReleaseConnection(MongoConnection connection)
        {
            _connectionPool.ReleaseConnection(connection);
        }

        /// <summary>
        /// Sets the state.
        /// </summary>
        /// <param name="state">The state.</param>
        internal void SetState(MongoServerState state)
        {
            SetState(state, Interlocked.CompareExchange(ref _stateInfo, null, null));
        }

        // private methods
        private void LookupServerInformation(MongoConnection connection)
        {
            IsMasterResult isMasterResult = null;
            bool ok = false;
            try
            {
                var isMasterCommand = new CommandDocument("ismaster", 1);
                var tempResult = connection.RunCommand("admin", QueryFlags.SlaveOk, isMasterCommand, false);
                isMasterResult = new IsMasterResult();
                isMasterResult.Initialize(isMasterCommand, tempResult.Response);
                if (!isMasterResult.Ok)
                {
                    throw new MongoCommandException(isMasterResult);
                }

                MongoServerBuildInfo buildInfo;
                var buildInfoCommand = new CommandDocument("buildinfo", 1);
                var buildInfoResult = connection.RunCommand("admin", QueryFlags.SlaveOk, buildInfoCommand, false);
                if (buildInfoResult.Ok)
                {
                    buildInfo = MongoServerBuildInfo.FromCommandResult(buildInfoResult);
                }
                else
                {
                    // short term fix: if buildInfo fails due to auth we don't know the server version; see CSHARP-324
                    if (buildInfoResult.ErrorMessage != "need to login")
                    {
                        throw new MongoCommandException(buildInfoResult);
                    }
                    buildInfo = null;
                }

                ReplicaSetInformation replicaSetInformation = null;
                MongoServerInstanceType instanceType = MongoServerInstanceType.StandAlone;
                if (isMasterResult.ReplicaSetName != null)
                {
                    var tagSet = new ReplicaSetTagSet();
                    var peers = isMasterResult.Hosts.Concat(isMasterResult.Passives).Concat(isMasterResult.Arbiters).ToList();
                    replicaSetInformation = new ReplicaSetInformation(isMasterResult.ReplicaSetName, isMasterResult.Primary, peers, tagSet);
                    instanceType = MongoServerInstanceType.ReplicaSetMember;
                }
                else if (isMasterResult.Message != null && isMasterResult.Message == "isdbgrid")
                {
                    instanceType = MongoServerInstanceType.ShardRouter;
                }

                var newStateInfo = new ServerInformation
                {
                    BuildInfo = buildInfo,
                    InstanceType = instanceType,
                    IsArbiter = isMasterResult.IsArbiterOnly,
                    IsMasterResult = isMasterResult,
                    IsPassive = isMasterResult.IsPassive,
                    IsPrimary = isMasterResult.IsPrimary,
                    IsSecondary = isMasterResult.IsSecondary,
                    MaxDocumentSize = isMasterResult.MaxBsonObjectSize,
                    MaxMessageLength = isMasterResult.MaxMessageLength,
                    ReplicaSetInformation = replicaSetInformation
                };
                MongoServerState currentState;
                lock (_stateLock)
                {
                    currentState = _state;
                }
                SetState(currentState, newStateInfo);
                ok = true;
            }
            finally
            {
                if (!ok)
                {
                    var currentStateInfo = Interlocked.CompareExchange(ref _stateInfo, null, null);
                    var newStateInfo = new ServerInformation
                    {
                        BuildInfo = null,
                        InstanceType = currentStateInfo.InstanceType,
                        IsArbiter = false,
                        IsMasterResult = isMasterResult,
                        IsPassive = false,
                        IsPrimary = false,
                        IsSecondary = false,
                        MaxDocumentSize = MongoDefaults.MaxDocumentSize,
                        MaxMessageLength = MongoDefaults.MaxMessageLength,
                        ReplicaSetInformation = null
                    };

                    SetState(MongoServerState.Disconnected, newStateInfo);
                }
            }
        }

        private void OnAveragePingTimeChanged()
        {
            if (AveragePingTimeChanged != null)
            {
                try { AveragePingTimeChanged(this, EventArgs.Empty); }
                catch { } // ignore exceptions
            }
        }

        private void OnStateChanged()
        {
            if (StateChanged != null)
            {
                try { StateChanged(this, null); }
                catch { } // ignore exceptions
            }
        }

        private void Ping(MongoConnection connection)
        {
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                var pingCommand = new CommandDocument("ping", 1);
                connection.RunCommand("admin", QueryFlags.SlaveOk, pingCommand, true);
                stopwatch.Stop();
                var currentAverage = _pingTimeAggregator.Average;
                _pingTimeAggregator.Include(stopwatch.Elapsed);
                var newAverage = _pingTimeAggregator.Average;
                if (currentAverage != newAverage)
                {
                    OnAveragePingTimeChanged();
                }
            }
            catch
            {
                _pingTimeAggregator.Clear();
                SetState(MongoServerState.Disconnected);
                throw;
            }
        }

        private void StateVerificationTimerCallback()
        {
            if (_inStateVerification)
            {
                return;
            }

            _inStateVerification = true;
            try
            {
                var connection = new MongoConnection(this);
                try
                {
                    Ping(connection);
                    LookupServerInformation(connection);
                    ThreadPool.QueueUserWorkItem(o => _connectionPool.MaintainPoolSize());
                    SetState(MongoServerState.Connected);
                }
                finally
                {
                    connection.Close();
                }
            }
            catch { } // this is called in a timer thread and we don't want any exceptions escaping
            finally
            {
                _inStateVerification = false;
            }
        }

        private void SetState(MongoServerState state, ServerInformation serverInfo)
        {
            var currentServerInfo = Interlocked.CompareExchange(ref _stateInfo, null, null);

            bool raiseChangedEvent = false;
            lock (_stateLock)
            {
                if (_state != state)
                {
                    _state = state;
                    raiseChangedEvent = true;
                }

                if (state == MongoServerState.Disconnected)
                {
                    _connectionPool.Clear();
                }

                if (currentServerInfo != serverInfo && currentServerInfo.IsDifferentFrom(serverInfo))
                {
                    Interlocked.Exchange(ref _stateInfo, serverInfo);
                    raiseChangedEvent = true;
                }
            }

            if (raiseChangedEvent)
            {
                OnStateChanged();
            }
        }

        // NOTE: while all these properties are mutable, it is purely for ease of use.  This class is used as an immutable class.
        private class ServerInformation
        {
            public MongoServerBuildInfo BuildInfo { get; set; }

            public MongoServerInstanceType InstanceType { get; set; }

            public bool IsArbiter { get; set; }

            public IsMasterResult IsMasterResult { get; set; }

            public bool IsPassive { get; set; }

            public bool IsPrimary { get; set; }

            public bool IsSecondary { get; set; }

            public int MaxDocumentSize { get; set; }

            public int MaxMessageLength { get; set; }

            public ReplicaSetInformation ReplicaSetInformation { get; set; }

            public bool IsDifferentFrom(ServerInformation other)
            {
                if (InstanceType != other.InstanceType)
                {
                    return true;
                }

                if (IsPrimary != other.IsPrimary)
                {
                    return true;
                }

                if (IsSecondary != other.IsSecondary)
                {
                    return true;
                }

                if (IsPassive != other.IsPassive)
                {
                    return true;
                }

                if (IsArbiter != other.IsArbiter)
                {
                    return true;
                }

                if (MaxDocumentSize != other.MaxDocumentSize)
                {
                    return true;
                }

                if (MaxMessageLength != other.MaxMessageLength)
                {
                    return true;
                }

                if ((ReplicaSetInformation == null && other.ReplicaSetInformation != null) || (ReplicaSetInformation != other.ReplicaSetInformation))
                {
                    return true;
                }

                return false;
            }
        }
    }
}
