﻿/*
 * Copyright (c) 2012-2019 Snowflake Computing Inc. All rights reserved.
 */

using System;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Snowflake.Data.Log;
using Snowflake.Data.Client;
using System.Text;
using System.Web;

namespace Snowflake.Data.Core.Authenticator
{
    /// <summary>
    /// OktaAuthenticator would perform serveral steps of authentication with Snowflake and Okta idp
    /// </summary>
    class OktaAuthenticator : IAuthenticator
    {
        private static readonly SFLogger logger = SFLoggerFactory.GetLogger<OktaAuthenticator>();
        /// <summary>
        /// Session that create this authenticator
        /// </summary>
        private SFSession session;
        /// <summary>
        /// url of the okta idp
        /// </summary>
        private Uri oktaUrl;

        /// <summary>
        /// Constructor of the Okta authenticator
        /// </summary>
        /// <param name="session"></param>
        /// <param name="oktaUriString"></param>
        internal OktaAuthenticator(SFSession session, string oktaUriString)
        {
            this.session = session;
            oktaUrl = new Uri(oktaUriString);
        }

        /// <see cref="IAuthenticator"/>
        async Task IAuthenticator.AuthenticateAsync(CancellationToken cancellationToken)
        {
            logger.Info("Okta Authentication");

            logger.Debug("step 1: get sso and token url");
            var authenticatorRestRequest = BuildAuthenticatorRestRequest();
            var authenticatorResponse = await session.restRequester.PostAsync<AuthnResponse>(authenticatorRestRequest, cancellationToken);
            FilterFailedResponse(authenticatorResponse);
            Uri ssoUrl = new Uri(authenticatorResponse.data.ssoUrl);
            Uri tokenUrl = new Uri(authenticatorResponse.data.tokenUrl);

            logger.Debug("step 2: verify urls fetched from step 1");
            logger.Debug("Checking sso url");
            VerifyUrls(ssoUrl, oktaUrl);
            logger.Debug("Checking token url");
            VerifyUrls(tokenUrl, oktaUrl);

            logger.Debug("step 3: get idp onetime token");
            IdpTokenRestRequest idpTokenRestRequest = BuildIdpTokenRestRequest(tokenUrl);
            var idpResponse = await session.restRequester.PostAsync<IdpTokenResponse>(idpTokenRestRequest, cancellationToken);
            string onetimeToken = idpResponse.CookieToken;

            logger.Debug("step 4: get SAML reponse from sso");
            var samlRestRequest = BuildSAMLRestRequest(ssoUrl, onetimeToken);
            var samlRawResponse = await session.restRequester.GetAsync(samlRestRequest, cancellationToken);
            var samlRawHtmlString = samlRawResponse.Content.ReadAsStringAsync().Result;

            logger.Debug("step 5: verify postback url in SAML reponse");
            VerifyPostbackUrl(samlRawHtmlString);

            logger.Debug("step 6: send SAML reponse to snowflake to login");
            var loginRestRequest = BuildOktaLoginRestRequest(samlRawHtmlString);
            var authnResponse = await session.restRequester.PostAsync<AuthnResponse>(loginRestRequest, cancellationToken);
            session.ProcessLoginResponse(authnResponse);   
        }

        private SFRestRequest BuildAuthenticatorRestRequest()
        {
            var fedUrl = session.BuildUri(RestPath.SF_AUTHENTICATOR_REQUEST_PATH);
            var data = new AuthnRequestData()
            {
                accountName = session.properties[SFSessionProperty.ACCOUNT],
                Authenticator = oktaUrl.ToString(),
                clientAppId = ".NET",
                clientAppVersion = SFEnvironment.Version,
                clientEnv = SFEnvironment.ClientEnv,
            };

            int connectionTimeoutSec = int.Parse(session.properties[SFSessionProperty.CONNECTION_TIMEOUT]);

            return session.BuildTimeoutRestRequest(fedUrl, new AuthnRequest() { data = data });
        }

        private IdpTokenRestRequest BuildIdpTokenRestRequest(Uri tokenUrl)
        {
            return new IdpTokenRestRequest()
            {
                Url = tokenUrl,
                RestTimeout = session.connectionTimeout,
                HttpTimeout = TimeSpan.FromSeconds(16),
                JsonBody = new IdpTokenRequest()
                {
                    Username = session.properties[SFSessionProperty.USER],
                    Password = session.properties[SFSessionProperty.PASSWORD],
                },
            };
        }

        private SAMLRestRequest BuildSAMLRestRequest(Uri ssoUrl, string onetimeToken)
        {
            return new SAMLRestRequest()
            {
                Url = ssoUrl,
                RestTimeout = session.connectionTimeout,
                HttpTimeout = Timeout.InfiniteTimeSpan,
                OnetimeToken = onetimeToken,
            };
        }

        private SFRestRequest BuildOktaLoginRestRequest(string samlRawHtmlString)
        {
            // build uri
            var loginUrl = session.BuildLoginUrl();

            AuthnRequestData data = new AuthnRequestData()
            {
                loginName = session.properties[SFSessionProperty.USER],
                password = session.properties[SFSessionProperty.PASSWORD],
                accountName = session.properties[SFSessionProperty.ACCOUNT],
                clientAppId = ".NET",
                clientAppVersion = SFEnvironment.Version,
                clientEnv = SFEnvironment.ClientEnv,
                RawSamlResponse = samlRawHtmlString,
            };

            int connectionTimeoutSec = int.Parse(session.properties[SFSessionProperty.CONNECTION_TIMEOUT]);

            return session.BuildTimeoutRestRequest(loginUrl, new AuthnRequest() { data = data });
        }

        private void VerifyUrls(Uri tokenOrSsoUrl, Uri sessionUrl)
        {
            if (tokenOrSsoUrl.Scheme != sessionUrl.Scheme || tokenOrSsoUrl.Host != sessionUrl.Host)
            {
                var e = new SnowflakeDbException(
                    SFError.IDP_SSO_TOKEN_URL_MISMATCH, tokenOrSsoUrl.ToString(), oktaUrl.ToString());
                logger.Error("Different urls", e);
                throw e;
            }
        }

        private void VerifyPostbackUrl(string samlRawHtmlString)
        {
            int formIndex = samlRawHtmlString.IndexOf("<form");
            bool extractSuccess = formIndex == -1;

            // skip 'action="' (length = 8)
            int startIndex = samlRawHtmlString.IndexOf("action=", formIndex) + 8;
            int length = samlRawHtmlString.IndexOf('"', startIndex) - startIndex;

            Uri postBackUrl;
            try
            {
                postBackUrl = new Uri(HttpUtility.HtmlDecode(samlRawHtmlString.Substring(startIndex, length)));
            } catch (Exception e)
            {
                logger.Error("Fail to extract SAML from html", e);
                throw new SnowflakeDbException(SFError.IDP_SAML_POSTBACK_NOTFOUND);
            }

            string sessionHost = session.properties[SFSessionProperty.HOST];
            string sessionScheme = session.properties[SFSessionProperty.SCHEME];
            if (postBackUrl.Host != sessionHost ||
                postBackUrl.Scheme != sessionScheme)
            {
                var e = new SnowflakeDbException(
                    SFError.IDP_SAML_POSTBACK_INVALID,
                    postBackUrl.ToString(),
                    sessionScheme + ":\\\\" + sessionHost);
                logger.Error("Different urls", e);
                throw e;
            }
        }

        private void FilterFailedResponse(BaseRestResponse response)
        {
            if (!response.success)
            {
                SnowflakeDbException e = new SnowflakeDbException("", response.code, response.message, "");
                logger.Error("Authentication failed", e);
                throw e;
            }
        }
    }

    internal class IdpTokenRestRequest : BaseRestRequest, IRestRequest
    {   
        private static MediaTypeWithQualityHeaderValue jsonHeader = new MediaTypeWithQualityHeaderValue("application/json");

        internal IdpTokenRequest JsonBody { get; set; }

        HttpRequestMessage IRestRequest.ToRequestMessage(HttpMethod method)
        {
            HttpRequestMessage message = newMessage(method, Url);
            message.Headers.Accept.Add(jsonHeader);

            var json = JsonConvert.SerializeObject(JsonBody);
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");

            return message;
        }
    }

    class IdpTokenRequest
    {
        [JsonProperty(PropertyName = "username")]
        internal String Username { get; set; }

        [JsonProperty(PropertyName = "password")]
        internal String Password { get; set; }
    }

    class IdpTokenResponse
    {
        [JsonProperty(PropertyName = "cookieToken")]
        internal String CookieToken { get; set; }
    }

    class SAMLRestRequest : BaseRestRequest, IRestRequest
    {
        internal string OnetimeToken { set; get; }
        HttpRequestMessage IRestRequest.ToRequestMessage(HttpMethod method)
        {
            UriBuilder builder = new UriBuilder(Url);
            builder.Query = "RelayState=%2Fsome%2Fdeep%2Flink&onetimetoken=" + OnetimeToken;
            HttpRequestMessage message = newMessage(method, builder.Uri);

            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            return message;
        }
    }
}