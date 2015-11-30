﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.Text;

namespace WcfService.TestResources
{
    public abstract class DigestServiceAuthorizationManager : ServiceAuthorizationManager, IServiceBehavior
    {
        private readonly string _realm;
        private readonly TimeSpan _nonceValidityTime = TimeSpan.FromMinutes(1);

        public DigestServiceAuthorizationManager(string realm)
        {
            if (string.IsNullOrEmpty(realm))
            {
                throw new ArgumentNullException("realm");
            }

            _realm = realm;
        }

        private bool Authorized(DigestAuthenticationState digestState, OperationContext operationContext, ref Message message)
        {
            object identitiesListObject;
            if (!operationContext.ServiceSecurityContext.AuthorizationContext.Properties.TryGetValue("Identities",
                out identitiesListObject))
            {
                identitiesListObject = new List<IIdentity>(1);
                operationContext.ServiceSecurityContext.AuthorizationContext.Properties.Add("Identities", identitiesListObject);
            }

            var identities = identitiesListObject as IList<IIdentity>;
            identities.Add(new GenericIdentity(digestState.Username, "GenericPrincipal"));

            return true;
        }

        public override bool CheckAccess(OperationContext operationContext, ref Message message)
        {
            var digestState = new DigestAuthenticationState(operationContext, _realm);
            if (!digestState.IsRequestDigestAuth)
            {
                return UnauthorizedResponse(digestState);
            }

            string password;
            if (!GetPassword(digestState.Username, out password))
            {
                return UnauthorizedResponse(digestState);
            }

            digestState.Password = password;
            if (!digestState.Authorized || digestState.IsNonceStale)
            {
                return UnauthorizedResponse(digestState);
            }

            return Authorized(digestState, operationContext, ref message);
        }

        protected virtual DateTime GetNonceExpiryTime()
        {
            return DateTime.Now + _nonceValidityTime;
        }

        public abstract bool GetPassword(string username, out string password);

        private bool UnauthorizedResponse(DigestAuthenticationState digestState)
        {
            digestState.NonceExpiryTime = GetNonceExpiryTime();
            digestState.SetChallengeResponse(HttpStatusCode.Unauthorized, "Access Denied");
            return false;
        }

        #region IServiceBehavior
        public void Validate(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase) { }

        public void AddBindingParameters(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase, Collection<ServiceEndpoint> endpoints,
            BindingParameterCollection bindingParameters) { }

        public void ApplyDispatchBehavior(ServiceDescription serviceDescription, ServiceHostBase serviceHostBase)
        {
            serviceHostBase.Authorization.ServiceAuthorizationManager = this;
            foreach (ChannelDispatcherBase t in serviceHostBase.ChannelDispatchers)
            {
                ChannelDispatcher channelDispatcher = t as ChannelDispatcher;
                foreach (EndpointDispatcher endpointDispatcher in channelDispatcher.Endpoints)
                {
                    endpointDispatcher.DispatchRuntime.ServiceAuthorizationManager = this;
                }
            }
        }
        #endregion

        private struct DigestAuthenticationState
        {
            private const string DigestAuthenticationMechanism = "Digest ";
            private const int DigestAuthenticationMechanismLength = 7; // DigestAuthenticationMechanism.Length;
            private const string UriAuthenticationParameter = "uri";
            private const string UsernameAuthenticationParameter = "username";
            private const string NonceAuthenticationParameter = "nonce";
            private const string RealmAuthenticationParameter = "realm";
            private const string ResponseAuthenticationParameter = "response";
            private const string AuthenticationChallengeHeaderName = "WWW-Authenticate";
            private const string AuthorizationHeaderName = "Authorization";
            private readonly string _authorizationHeader;
            private readonly OperationContext _operationContext;
            private readonly Dictionary<string, string> _authorizationParameters;
            private readonly string _method;
            private readonly string _realm;
            private string _nonceString;
            private string _password;
            private bool? _authorized;

            public DigestAuthenticationState(OperationContext operationContext, string realm)
            {
                _operationContext = operationContext;
                _realm = realm;
                _password = null;
                _authorized = new bool?();
                _authorizationHeader = GetAuthorizationHeader(operationContext, out _method);
                if (_authorizationHeader.Length < DigestAuthenticationMechanismLength)
                {
                    _authorized = false;
                    _nonceString = string.Empty;
                    _authorizationParameters = new Dictionary<string, string>();
                    return;
                }

                string[] digestElements = _authorizationHeader.Substring(DigestAuthenticationMechanismLength).Split(',');
                _authorizationParameters = new Dictionary<string, string>(digestElements.Length);
                foreach (string element in digestElements)
                {
                    string[] keyValue = element.Split(new[] { '=' }, 2);
                    string key = keyValue[0].Trim(' ', '\"');
                    string value = keyValue[1].Trim(' ', '\"');
                    _authorizationParameters.Add(key, value);
                }

                _nonceString = _authorizationParameters[NonceAuthenticationParameter];
            }

            public bool Authorized
            {
                get
                {
                    if (!_authorized.HasValue)
                    {
                        VerifyAuthorization();
                    }

                    return _authorized.Value;
                }
            }

            public bool IsRequestDigestAuth { get { return _authorizationHeader.StartsWith(DigestAuthenticationMechanism); } }

            public string Nonce { get { return _nonceString; } }

            public DateTime NonceExpiryTime
            {
                get
                {
                    DateTime expiry;

                    // Make sure the base64 value is padded right as nonce's can't
                    // have the character = at the end.
                    var nonce = Nonce;
                    int remainder = nonce.Length % 4;
                    if (remainder > 0)
                    {
                        int padCount = 4 - remainder;
                        nonce = nonce + new string('=', padCount);
                    }

                    try
                    {
                        byte[] nonceBytes = Convert.FromBase64String(nonce);
                        string expiryString = Encoding.ASCII.GetString(nonceBytes);
                        expiry = DateTime.Parse(expiryString);
                    }
                    catch (FormatException)
                    {
                        return DateTime.MinValue;
                    }

                    return expiry;
                }
                set
                {
                    string nonceExpiryString = value.ToString("G");
                    byte[] nonceExpiryBytes = Encoding.ASCII.GetBytes(nonceExpiryString);
                    string nonceString = Convert.ToBase64String(nonceExpiryBytes);
                    _nonceString = nonceString.TrimEnd('=');
                }
            }

            public Dictionary<string, string> Parameters { get { return _authorizationParameters; } }

            public string Password { set { _password = value; } }

            public bool IsNonceStale { get { return NonceExpiryTime < DateTime.Now; } }

            public string Uri { get { return Parameters[UriAuthenticationParameter]; } }

            public string Username { get { return Parameters[UsernameAuthenticationParameter]; } }

            private string CalculateHash(string plaintext)
            {
                Encoding enc = Encoding.ASCII;
                MD5 md5 = MD5.Create();
                byte[] hashbytes = md5.ComputeHash(enc.GetBytes(plaintext));
                var hashStringBuilder = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    hashStringBuilder.AppendFormat("{0:x02}", hashbytes[i]);
                }

                return hashStringBuilder.ToString();
            }

            public void SetChallengeResponse(HttpStatusCode statusCode, string statusDescription)
            {
                StringBuilder authChallenge = new StringBuilder(DigestAuthenticationMechanism);
                authChallenge.AppendFormat(RealmAuthenticationParameter + "=\"{0}\", ", _realm);
                authChallenge.AppendFormat(NonceAuthenticationParameter + "=\"{0}\", ", Nonce);
                authChallenge.Append("opaque=\"0000000000000000\", ");
                authChallenge.AppendFormat("stale={0}, ", IsNonceStale ? "true" : "false");
                authChallenge.Append("algorithm=MD5, qop=\"auth\"");

                object responsePropertyObject;
                if (!_operationContext.OutgoingMessageProperties.TryGetValue(HttpResponseMessageProperty.Name, out responsePropertyObject))
                {
                    responsePropertyObject = new HttpResponseMessageProperty();
                    _operationContext.OutgoingMessageProperties[HttpResponseMessageProperty.Name] = responsePropertyObject;
                }

                var responseMessageProperty = (HttpResponseMessageProperty)responsePropertyObject;
                responseMessageProperty.Headers[AuthenticationChallengeHeaderName] = authChallenge.ToString();
                responseMessageProperty.StatusCode = statusCode;
                responseMessageProperty.StatusDescription = statusDescription;
            }

            private void VerifyAuthorization()
            {
                if (_authorized.HasValue)
                {
                    return;
                }

                if (_realm == null || Username == null || _password == null || _method == null)
                {
                    throw new InvalidOperationException("Insufficient information to determine authorization");
                }

                string SA1 = string.Concat(Username, ":", _realm, ":", _password);
                string HA1 = CalculateHash(SA1);
                string SA2 = string.Concat(_method, ":", Uri);
                string HA2 = CalculateHash(SA2);

                string responseString;
                if (Parameters["qop"] != null)
                {
                    responseString = string.Concat(
                        HA1, ":",
                        Nonce, ":",
                        Parameters["nc"], ":",
                        Parameters["cnonce"], ":",
                        Parameters["qop"], ":",
                        HA2);
                }
                else
                {
                    responseString = string.Concat(HA1, ":", Nonce, ":", HA2);
                }

                string responseDigest = CalculateHash(responseString);
                _authorized = (Parameters[ResponseAuthenticationParameter] == responseDigest);
            }

            private static string GetAuthorizationHeader(OperationContext operationContext, out string method)
            {
                object requestMessagePropertyObject;
                if (!operationContext.IncomingMessageProperties.TryGetValue(HttpRequestMessageProperty.Name,
                        out requestMessagePropertyObject))
                {
                    throw new InvalidOperationException("Not an HTTP request");
                }

                HttpRequestMessageProperty requestMessageProperties = (HttpRequestMessageProperty)requestMessagePropertyObject;
                method = requestMessageProperties.Method;
                var requestHeaders = requestMessageProperties.Headers;

                var authorizationHeader = requestHeaders[AuthorizationHeaderName];
                if (authorizationHeader == null)
                {
                    authorizationHeader = string.Empty;
                }

                return authorizationHeader.Trim();
            }
        }
    }
}
