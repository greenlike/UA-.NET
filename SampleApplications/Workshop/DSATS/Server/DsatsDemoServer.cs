/* ========================================================================
 * Copyright (c) 2005-2016 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using System;
using System.Collections.Generic;
using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using System.Text;
using System.Xml;
using System.Reflection;
using System.Runtime.InteropServices;
using Opc.Ua;
using Opc.Ua.Server;

namespace DsatsDemoServer
{
    /// <summary>
    /// Implements a basic Server.
    /// </summary
    public partial class DsatsDemoServer : StandardServer
    {
        #region Overridden DsatsDemo
        /// <summary>
        /// Initializes the server before it starts up.
        /// </summary>
        /// <remarks>
        /// This method is called before any startup processing occurs. The sub-class may update the 
        /// configuration object or do any other application specific startup tasks.
        /// </remarks>
        protected override void OnServerStarting(ApplicationConfiguration configuration)
        {
            base.OnServerStarting(configuration);
        }

        /// <summary>
        /// Called after the server has been started.
        /// </summary>
        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);

            // request notifications when the user identity is changed. all valid users are accepted by default.
            server.SessionManager.ImpersonateUser += new ImpersonateEventHandler(SessionManager_ImpersonateUser);
        }

        /// <summary>
        /// Creates the node managers for the server.
        /// </summary>
        /// <remarks>
        /// This method allows the sub-class create any additional node managers which it uses. The SDK
        /// always creates a CoreNodeManager which handles the built-in nodes defined by the specification.
        /// Any additional NodeManagers are expected to handle application specific nodes.
        /// </remarks>
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            Utils.Trace("Creating the Node Managers.");

            List<INodeManager> nodeManagers = new List<INodeManager>();

            // create the custom node managers.
            m_nodeManager = new DsatsDemoNodeManager(server, configuration);
            nodeManagers.Add(m_nodeManager);
            
            // create master node manager.
            return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
        }

        /// <summary>
        /// Loads the non-configurable properties for the application.
        /// </summary>
        /// <remarks>
        /// These properties are exposed by the server but cannot be changed by administrators.
        /// </remarks>
        protected override ServerProperties LoadServerProperties()
        {
            ServerProperties properties = new ServerProperties();

            properties.ManufacturerName = "OPC Foundation";
            properties.ProductName      = "OPC UA Quickstarts";
            properties.ProductUri       = "http://opcfoundation.org/DsatsDemoServer/";
            properties.SoftwareVersion  = Utils.GetAssemblySoftwareVersion();
            properties.BuildNumber      = Utils.GetAssemblyBuildNumber();
            properties.BuildDate        = Utils.GetAssemblyTimestamp();

            // TBD - All applications have software certificates that need to added to the properties.

            return properties;
        }
        #endregion

        #region User Authentication Support
        /// <summary>
        /// Called when a client tries to change its user identity.
        /// </summary>
        private void SessionManager_ImpersonateUser(Session session, ImpersonateEventArgs args)
        {
            // check for a user name token.
            UserNameIdentityToken userNameToken = args.NewIdentity as UserNameIdentityToken;

            if (userNameToken != null)
            {
                VerifyPassword(userNameToken.UserName, userNameToken.DecryptedPassword);
                args.Identity = new UserIdentity(userNameToken);
                Utils.Trace("UserName Token Accepted: {0}", args.Identity.DisplayName);
            }
        }

        /// <summary>
        /// Validates the password for a username token.
        /// </summary>
        private void VerifyPassword(string userName, string password)
        {
            IntPtr handle = IntPtr.Zero;

            const int LOGON32_PROVIDER_DEFAULT = 0;
            // const int LOGON32_LOGON_INTERACTIVE = 2;
            const int LOGON32_LOGON_NETWORK = 3;
            // const int LOGON32_LOGON_BATCH = 4;

            if (password == null)
            {
                password = String.Empty;
            }

            bool result = NativeMethods.LogonUser(
                userName,
                String.Empty,
                password,
                LOGON32_LOGON_NETWORK,
                LOGON32_PROVIDER_DEFAULT,
                ref handle);

            if (!result)
            {
                throw ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Login failed for user: {0}", userName);
            }

            NativeMethods.CloseHandle(handle);
        }

        /// <summary>
        /// Impersonates the windows user identifed by the security token.
        /// </summary>
        private void LogonUser(OperationContext context, UserNameSecurityToken securityToken)
        {
            IntPtr handle = IntPtr.Zero;

            const int LOGON32_PROVIDER_DEFAULT = 0;
            // const int LOGON32_LOGON_INTERACTIVE = 2;
            const int LOGON32_LOGON_NETWORK = 3;
            // const int LOGON32_LOGON_BATCH = 4;

            bool result = NativeMethods.LogonUser(
                securityToken.UserName, 
                String.Empty, 
                securityToken.Password,
                LOGON32_LOGON_NETWORK, 
                LOGON32_PROVIDER_DEFAULT,
                ref handle);

            if (!result)
            {
                throw ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Login failed for user: {0}", securityToken.UserName);
            }

            WindowsIdentity identity = new WindowsIdentity(handle);

            ImpersonationContext impersonationContext = new ImpersonationContext();
            impersonationContext.Handle = handle;
            impersonationContext.Context = identity.Impersonate();

            lock (this.m_lock)
            {
                m_contexts.Add(context.RequestId, impersonationContext);
            }
        }

        /// <summary>
        /// This method is called at the being of the thread that processes a request.
        /// </summary>
        protected override OperationContext ValidateRequest(RequestHeader requestHeader, RequestType requestType)
        {
            OperationContext context = base.ValidateRequest(requestHeader, requestType);

            if (context.UserIdentity != null)
            {
                SecurityToken securityToken = context.UserIdentity.GetSecurityToken();

                // check for a user name token.
                UserNameSecurityToken userNameToken = securityToken as UserNameSecurityToken;

                if (userNameToken != null)
                {
                    LogonUser(context, userNameToken);
                }
            }

            return context;
        }

        /// <summary>
        /// This method is called in a finally block at the end of request processing (i.e. called even on exception).
        /// </summary>
        protected override void OnRequestComplete(OperationContext context)
        {
           ImpersonationContext impersonationContext = null;

            lock (this.m_lock)
            {
                if (m_contexts.TryGetValue(context.RequestId, out impersonationContext))
                {
                    m_contexts.Remove(context.RequestId);
                }
            }

            if (impersonationContext != null)
            {
                impersonationContext.Context.Undo();
                impersonationContext.Dispose();
            }

            base.OnRequestComplete(context);
        }
        #endregion 

        #region ImpersonationContext Class
        /// <summary>
        /// Stores information about the user that is currently being impersonated.
        /// </summary>
        private class ImpersonationContext : IDisposable
        {
            public WindowsImpersonationContext Context;
            public IntPtr Handle;

            #region IDisposable Members
            /// <summary>
            /// The finializer implementation.
            /// </summary>
            ~ImpersonationContext()
            {
                Dispose(false);
            }

            /// <summary>
            /// Frees any unmanaged resources.
            /// </summary>
            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            /// <summary>
            /// An overrideable version of the Dispose.
            /// </summary>
            protected virtual void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Utils.SilentDispose(Context);
                }

                if (Handle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(Handle);
                    Handle = IntPtr.Zero;
                }
            }
            #endregion
        }
        #endregion 
        
        #region PInvoke Declarations
        private static class NativeMethods
        {
            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool LogonUser(
                string lpszUsername,
                string lpszDomain,
                string lpszPassword,
                int dwLogonType,
                int dwLogonProvider,
                ref IntPtr phToken);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
            public extern static bool CloseHandle(IntPtr handle);
        }
        #endregion 

        #region Private Fields
        private object m_lock = new object();
        private Dictionary<uint, ImpersonationContext> m_contexts = new Dictionary<uint, ImpersonationContext>();
        private DsatsDemoNodeManager m_nodeManager;
        #endregion 
    }
}
