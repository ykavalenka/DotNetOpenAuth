﻿//-----------------------------------------------------------------------
// <copyright file="MockHttpRequest.cs" company="Andrew Arnott">
//     Copyright (c) Andrew Arnott. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace DotNetOpenAuth.Test.Mocks {
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.IO;
	using System.Net;
	using System.Text;
	using System.Web;
	using DotNetOpenAuth.Messaging;
	using DotNetOpenAuth.OpenId;
	using DotNetOpenAuth.OpenId.RelyingParty;
	using DotNetOpenAuth.Test.OpenId;
	using DotNetOpenAuth.Yadis;

	internal class MockHttpRequest {
		private readonly Dictionary<Uri, DirectWebResponse> registeredMockResponses = new Dictionary<Uri, DirectWebResponse>();

		private MockHttpRequest(IDirectSslWebRequestHandler mockHandler) {
			ErrorUtilities.VerifyArgumentNotNull(mockHandler, "mockHandler");
			this.MockWebRequestHandler = mockHandler;
		}

		internal IDirectSslWebRequestHandler MockWebRequestHandler { get; private set; }

		internal static MockHttpRequest CreateUntrustedMockHttpHandler() {
			TestWebRequestHandler testHandler = new TestWebRequestHandler();
			UntrustedWebRequestHandler untrustedHandler = new UntrustedWebRequestHandler(testHandler);
			if (!untrustedHandler.WhitelistHosts.Contains("localhost")) {
				untrustedHandler.WhitelistHosts.Add("localhost");
			}
			MockHttpRequest mock = new MockHttpRequest(untrustedHandler);
			testHandler.Callback = mock.GetMockResponse;
			return mock;
		}

		internal void RegisterMockResponse(DirectWebResponse response) {
			ErrorUtilities.VerifyArgumentNotNull(response, "response");
			if (this.registeredMockResponses.ContainsKey(response.RequestUri)) {
				Logger.WarnFormat("Mock HTTP response already registered for {0}.", response.RequestUri);
			} else {
				this.registeredMockResponses.Add(response.RequestUri, response);
			}
		}

		internal void RegisterMockResponse(Uri requestUri, string contentType, string responseBody) {
			this.RegisterMockResponse(requestUri, requestUri, contentType, responseBody);
		}

		internal void RegisterMockResponse(Uri requestUri, Uri responseUri, string contentType, string responseBody) {
			this.RegisterMockResponse(requestUri, responseUri, contentType, new WebHeaderCollection(), responseBody);
		}

		internal void RegisterMockResponse(Uri requestUri, Uri responseUri, string contentType, WebHeaderCollection headers, string responseBody) {
			ErrorUtilities.VerifyArgumentNotNull(requestUri, "requestUri");
			ErrorUtilities.VerifyArgumentNotNull(responseUri, "responseUri");
			ErrorUtilities.VerifyNonZeroLength(contentType, "contentType");

			// Set up the redirect if appropriate
			if (requestUri != responseUri) {
				this.RegisterMockRedirect(requestUri, responseUri);
			}

			string contentEncoding = null;
			MemoryStream stream = new MemoryStream();
			StreamWriter sw = new StreamWriter(stream);
			sw.Write(responseBody);
			sw.Flush();
			stream.Seek(0, SeekOrigin.Begin);
			this.RegisterMockResponse(new CachedDirectWebResponse(responseUri, responseUri, headers ?? new WebHeaderCollection(), HttpStatusCode.OK, contentType, contentEncoding, stream));
		}

		internal void RegisterMockXrdsResponses(IDictionary<string, string> requestUriAndResponseBody) {
			foreach (var pair in requestUriAndResponseBody) {
				this.RegisterMockResponse(new Uri(pair.Key), "text/xml; saml=false; https=false; charset=UTF-8", pair.Value);
			}
		}

		internal void RegisterMockXrdsResponse(ServiceEndpoint endpoint) {
			ErrorUtilities.VerifyArgumentNotNull(endpoint, "endpoint");

			string identityUri;
			if (endpoint.ClaimedIdentifier == endpoint.Protocol.ClaimedIdentifierForOPIdentifier) {
				identityUri = endpoint.UserSuppliedIdentifier;
			} else {
				identityUri = endpoint.UserSuppliedIdentifier ?? endpoint.ClaimedIdentifier;
			}
			this.RegisterMockXrdsResponse(new Uri(identityUri), new ServiceEndpoint[] { endpoint });
		}

		internal void RegisterMockXrdsResponse(Uri respondingUri, IEnumerable<ServiceEndpoint> endpoints) {
			ErrorUtilities.VerifyArgumentNotNull(endpoints, "endpoints");

			StringBuilder xrds = new StringBuilder();
			xrds.AppendLine(@"<xrds:XRDS xmlns:xrds='xri://$xrds' xmlns:openid='http://openid.net/xmlns/1.0' xmlns='xri://$xrd*($v*2.0)'>
	<XRD>");
			foreach (var endpoint in endpoints) {
				string template = @"
		<Service priority='10'>
			<Type>{0}</Type>
			<URI>{1}</URI>
			<LocalID>{2}</LocalID>
			<openid:Delegate xmlns:openid='http://openid.net/xmlns/1.0'>{2}</openid:Delegate>
		</Service>";
				string serviceTypeUri;
				if (endpoint.ClaimedIdentifier == endpoint.Protocol.ClaimedIdentifierForOPIdentifier) {
					serviceTypeUri = endpoint.Protocol.OPIdentifierServiceTypeURI;
				} else {
					serviceTypeUri = endpoint.Protocol.ClaimedIdentifierServiceTypeURI;
				}
				string xrd = string.Format(
					CultureInfo.InvariantCulture,
					template,
					HttpUtility.HtmlEncode(serviceTypeUri),
					HttpUtility.HtmlEncode(endpoint.ProviderEndpoint.AbsoluteUri),
					HttpUtility.HtmlEncode(endpoint.ProviderLocalIdentifier));
				xrds.Append(xrd);
			}
			xrds.Append(@"
	</XRD>
</xrds:XRDS>");

			this.RegisterMockResponse(respondingUri, ContentTypes.Xrds, xrds.ToString());
		}

		internal void RegisterMockXrdsResponse(UriIdentifier directedIdentityAssignedIdentifier, ServiceEndpoint providerEndpoint) {
			ServiceEndpoint identityEndpoint = ServiceEndpoint.CreateForClaimedIdentifier(
				directedIdentityAssignedIdentifier,
				directedIdentityAssignedIdentifier,
				providerEndpoint.ProviderEndpoint,
				providerEndpoint.ProviderDescription,
				10,
				10);
			this.RegisterMockXrdsResponse(identityEndpoint);
		}

		internal Identifier RegisterMockXrdsResponse(string embeddedResourcePath) {
			UriIdentifier id = TestSupport.GetFullUrl(embeddedResourcePath);
			this.RegisterMockResponse(id, "application/xrds+xml", TestSupport.LoadEmbeddedFile(embeddedResourcePath));
			return id;
		}

		internal void RegisterMockRPDiscovery() {
			Uri rpRealmUri = TestSupport.Realm.UriWithWildcardChangedToWww;

			string template = @"<xrds:XRDS xmlns:xrds='xri://$xrds' xmlns:openid='http://openid.net/xmlns/1.0' xmlns='xri://$xrd*($v*2.0)'>
	<XRD>
		<Service priority='10'>
			<Type>{0}</Type>
			<URI>{1}</URI>
		</Service>
	</XRD>
</xrds:XRDS>";
			string xrds = string.Format(CultureInfo.InvariantCulture, template, HttpUtility.HtmlEncode(Protocol.V20.RPReturnToTypeURI), HttpUtility.HtmlEncode(rpRealmUri.AbsoluteUri));

			this.RegisterMockResponse(rpRealmUri, ContentTypes.Xrds, xrds);
		}

		internal void DeleteResponse(Uri requestUri) {
			this.registeredMockResponses.Remove(requestUri);
		}

		internal void RegisterMockRedirect(Uri origin, Uri redirectLocation) {
			var redirectionHeaders = new WebHeaderCollection {
				{ HttpResponseHeader.Location, redirectLocation.AbsoluteUri },
			};
			DirectWebResponse response = new CachedDirectWebResponse(origin, origin, redirectionHeaders, HttpStatusCode.Redirect, null, null, new MemoryStream());
			this.RegisterMockResponse(response);
		}

		private DirectWebResponse GetMockResponse(HttpWebRequest request) {
			DirectWebResponse response;
			if (this.registeredMockResponses.TryGetValue(request.RequestUri, out response)) {
				// reset response stream position so this response can be reused on a subsequent request.
				response.ResponseStream.Seek(0, SeekOrigin.Begin);
				return response;
			} else {
				////Assert.Fail("Unexpected HTTP request: {0}", uri);
				Logger.WarnFormat("Unexpected HTTP request: {0}", request.RequestUri);
				return new CachedDirectWebResponse(request.RequestUri, request.RequestUri, new WebHeaderCollection(), HttpStatusCode.NotFound, "text/html", null, new MemoryStream());
			}
		}
	}
}