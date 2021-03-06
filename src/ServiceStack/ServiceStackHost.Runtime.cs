﻿// Copyright (c) Service Stack LLC. All Rights Reserved.
// License: https://raw.github.com/ServiceStack/ServiceStack/master/license.txt


using System;
using System.Linq;
using System.Net;
using System.Web;
using ServiceStack.FluentValidation;
using ServiceStack.Host.Handlers;
using ServiceStack.Metadata;
using ServiceStack.MiniProfiler;
using ServiceStack.Support.WebHost;
using ServiceStack.Web;

namespace ServiceStack
{
    public abstract partial class ServiceStackHost
    {
        /// <summary>
        /// Apply PreRequest Filters for participating Custom Handlers, e.g. RazorFormat, MarkdownFormat, etc
        /// </summary>
        public virtual bool ApplyCustomHandlerRequestFilters(IRequest httpReq, IResponse httpRes)
        {
            return ApplyPreRequestFilters(httpReq, httpRes);
        }

        /// <summary>
        /// Applies the raw request filters. Returns whether or not the request has been handled 
        /// and no more processing should be done.
        /// </summary>
        /// <returns></returns>
        public virtual bool ApplyPreRequestFilters(IRequest httpReq, IResponse httpRes)
        {
            foreach (var requestFilter in PreRequestFilters)
            {
                requestFilter(httpReq, httpRes);
                if (httpRes.IsClosed) break;
            }

            return httpRes.IsClosed;
        }

        /// <summary>
        /// Applies the request filters. Returns whether or not the request has been handled 
        /// and no more processing should be done.
        /// </summary>
        /// <returns></returns>
        public virtual bool ApplyRequestFilters(IRequest httpReq, IResponse httpRes, object requestDto)
        {
            httpReq.ThrowIfNull("httpReq");
            httpRes.ThrowIfNull("httpRes");

            using (Profiler.Current.Step("Executing Request Filters"))
            {
                //Exec all RequestFilter attributes with Priority < 0
                var attributes = FilterAttributeCache.GetRequestFilterAttributes(requestDto.GetType());
                var i = 0;
                for (; i < attributes.Length && attributes[i].Priority < 0; i++)
                {
                    var attribute = attributes[i];
                    Container.AutoWire(attribute);
                    attribute.RequestFilter(httpReq, httpRes, requestDto);
                    Release(attribute);
                    if (httpRes.IsClosed) return httpRes.IsClosed;
                }

                //Exec global filters
                foreach (var requestFilter in GlobalRequestFilters)
                {
                    requestFilter(httpReq, httpRes, requestDto);
                    if (httpRes.IsClosed) return httpRes.IsClosed;
                }

                //Exec remaining RequestFilter attributes with Priority >= 0
                for (; i < attributes.Length; i++)
                {
                    var attribute = attributes[i];
                    Container.AutoWire(attribute);
                    attribute.RequestFilter(httpReq, httpRes, requestDto);
                    Release(attribute);
                    if (httpRes.IsClosed) return httpRes.IsClosed;
                }

                return httpRes.IsClosed;
            }
        }

        /// <summary>
        /// Applies the response filters. Returns whether or not the request has been handled 
        /// and no more processing should be done.
        /// </summary>
        /// <returns></returns>
        public virtual bool ApplyResponseFilters(IRequest httpReq, IResponse httpRes, object response)
        {
            httpReq.ThrowIfNull("httpReq");
            httpRes.ThrowIfNull("httpRes");

            using (Profiler.Current.Step("Executing Response Filters"))
            {
                var responseDto = response.GetResponseDto();
                var attributes = responseDto != null
                    ? FilterAttributeCache.GetResponseFilterAttributes(responseDto.GetType())
                    : null;

                //Exec all ResponseFilter attributes with Priority < 0
                var i = 0;
                if (attributes != null)
                {
                    for (; i < attributes.Length && attributes[i].Priority < 0; i++)
                    {
                        var attribute = attributes[i];
                        Container.AutoWire(attribute);
                        attribute.ResponseFilter(httpReq, httpRes, response);
                        Release(attribute);
                        if (httpRes.IsClosed) return httpRes.IsClosed;
                    }
                }

                //Exec global filters
                foreach (var responseFilter in GlobalResponseFilters)
                {
                    responseFilter(httpReq, httpRes, response);
                    if (httpRes.IsClosed) return httpRes.IsClosed;
                }

                //Exec remaining RequestFilter attributes with Priority >= 0
                if (attributes != null)
                {
                    for (; i < attributes.Length; i++)
                    {
                        var attribute = attributes[i];
                        Container.AutoWire(attribute);
                        attribute.ResponseFilter(httpReq, httpRes, response);
                        Release(attribute);
                        if (httpRes.IsClosed) return httpRes.IsClosed;
                    }
                }

                return httpRes.IsClosed;
            }
        }

        public MetadataPagesConfig MetadataPagesConfig
        {
            get
            {
                return new MetadataPagesConfig(
                    Metadata,
                    Config.ServiceEndpointsMetadataConfig,
                    Config.IgnoreFormatsInMetadata,
                    ContentTypes.ContentTypeFormats.Keys.ToList());
            }
        }

        public virtual TimeSpan GetDefaultSessionExpiry()
        {
            var authFeature = this.GetPlugin<AuthFeature>();
            if (authFeature != null)
                return authFeature.GetDefaultSessionExpiry();

            var sessionFeature = this.GetPlugin<SessionFeature>();
            return sessionFeature != null 
                ? sessionFeature.SessionExpiry 
                : SessionFeature.DefaultSessionExpiry;
        }

        public bool HasFeature(Feature feature)
        {
            return (feature & Config.EnableFeatures) == feature;
        }

        public void AssertFeatures(Feature usesFeatures)
        {
            if (Config.EnableFeatures == Feature.All) return;

            if (!HasFeature(usesFeatures))
            {
                throw new UnauthorizedAccessException(
                    String.Format("'{0}' Features have been disabled by your administrator", usesFeatures));
            }
        }

        public void AssertContentType(string contentType)
        {
            if (Config.EnableFeatures == Feature.All) return;

            AssertFeatures(contentType.ToFeature());
        }

        public bool HasAccessToMetadata(IRequest httpReq, IResponse httpRes)
        {
            if (!HasFeature(Feature.Metadata))
            {
                HandleErrorResponse(httpReq, httpRes, HttpStatusCode.Forbidden, "Metadata Not Available");
                return false;
            }

            if (Config.MetadataVisibility != RequestAttributes.Any)
            {
                var actualAttributes = httpReq.GetAttributes();
                if ((actualAttributes & Config.MetadataVisibility) != Config.MetadataVisibility)
                {
                    HandleErrorResponse(httpReq, httpRes, HttpStatusCode.Forbidden, "Metadata Not Visible");
                    return false;
                }
            }
            return true;
        }

        public void HandleErrorResponse(IRequest httpReq, IResponse httpRes, HttpStatusCode errorStatus, string errorStatusDescription = null)
        {
            if (httpRes.IsClosed) return;

            httpRes.StatusDescription = errorStatusDescription;

            var handler = GetCustomErrorHandler(errorStatus)
                ?? Config.GlobalHtmlErrorHttpHandler
                ?? GetNotFoundHandler();

            handler.ProcessRequest(httpReq, httpRes, httpReq.OperationName);
        }

        public IServiceStackHandler GetCustomErrorHandler(int errorStatusCode)
        {
            try
            {
                return GetCustomErrorHandler((HttpStatusCode)errorStatusCode);
            }
            catch
            {
                return null;
            }
        }

        public IServiceStackHandler GetCustomErrorHandler(HttpStatusCode errorStatus)
        {
            IServiceStackHandler httpHandler = null;
            if (CustomErrorHttpHandlers != null)
            {
                CustomErrorHttpHandlers.TryGetValue(errorStatus, out httpHandler);
            }

            switch (errorStatus)
            {
                case HttpStatusCode.Forbidden:
                    return httpHandler ?? new ForbiddenHttpHandler();
                case HttpStatusCode.NotFound:
                    return httpHandler ?? new NotFoundHttpHandler();
            }

            return httpHandler;
        }

        public IServiceStackHandler GetNotFoundHandler()
        {
            IServiceStackHandler httpHandler = null;
            if (CustomErrorHttpHandlers != null)
            {
                CustomErrorHttpHandlers.TryGetValue(HttpStatusCode.NotFound, out httpHandler);
            }

            return httpHandler ?? new NotFoundHttpHandler();
        }

        public IHttpHandler GetCustomErrorHttpHandler(HttpStatusCode errorStatus)
        {
            var ssHandler = GetCustomErrorHandler(errorStatus)
                ?? GetNotFoundHandler();
            if (ssHandler == null) return null;
            var httpHandler = ssHandler as IHttpHandler;
            return httpHandler ?? new ServiceStackHttpHandler(ssHandler);
        }

        public bool HasValidAuthSecret(IRequest httpReq)
        {
            if (Config.AdminAuthSecret != null)
            {
                var authSecret = httpReq.GetParam("authsecret");
                return authSecret == Config.AdminAuthSecret;
            }

            return false;
        }

        public virtual void OnExceptionTypeFilter(Exception ex, ResponseStatus responseStatus)
        { 
            var argEx = ex as ArgumentException;
            var isValidationSummaryEx = argEx is ValidationException;
            if (argEx != null && !isValidationSummaryEx && argEx.ParamName != null)
            {
                var paramMsgIndex = argEx.Message.LastIndexOf("Parameter name:");
                var errorMsg = paramMsgIndex > 0
                    ? argEx.Message.Substring(0, paramMsgIndex)
                    : argEx.Message;

                responseStatus.Errors.Add(new ResponseError {
                    ErrorCode = ex.GetType().Name,
                    FieldName = argEx.ParamName,
                    Message = errorMsg,
                });
            }
        }
    }

}