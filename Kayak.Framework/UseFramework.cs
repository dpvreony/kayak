﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Disposables;
using Kayak;
using LitJson;
using Kayak.Core;

namespace Kayak.Framework
{
    public interface IKayakFrameworkBehavior
    {
        IObservable<Unit> Route(IKayakContext context);
        IObservable<bool> Authenticate(IKayakContext context);
        IObservable<Unit> Bind(IKayakContext context);
        IObservable<Unit> Handle(IKayakContext context);
    }

    public static partial class Extensions
    {
        public static IDisposable UseFramework(this IObservable<ISocket> sockets)
        {
            TypedJsonMapper jsonMapper = new TypedJsonMapper();
            jsonMapper.AddDefaultInputConversions();
            jsonMapper.AddDefaultOutputConversions();

            return sockets.UseFramework(new KayakFrameworkBehavior(Assembly.GetCallingAssembly().GetTypes()));
        }

        public static IDisposable UseFramework(this IObservable<ISocket> sockets, IKayakFrameworkBehavior behavior)
        {
            return sockets.Subscribe(SocketObserver(behavior));
        }

        static IObserver<ISocket> SocketObserver(IKayakFrameworkBehavior behavior)
        {
            return Observer.Create<ISocket>(s =>
                {
                    var c = KayakContext.CreateContext(s);
                    var process = c.ProcessContext(behavior)
                        .Subscribe(cx =>
                        {
                        },
                        e =>
                        {
                            Console.WriteLine("Error during context.");
                            Console.Out.WriteException(e);
                        },
                        () =>
                        {
                            Console.WriteLine("[{0}] {1} {2} {3} : {4} {5} {6}", DateTime.Now,
                            c.Request.Verb, c.Request.GetPath(), c.Request.HttpVersion,
                            c.Response.HttpVersion, c.Response.StatusCode, c.Response.ReasonPhrase);
                        });
                },
                e =>
                {
                    Console.Out.WriteLine("Error from socket source.");
                    Console.Out.WriteException(e);
                },
                () => { Console.Out.WriteLine("Socket source completed."); });
        }

        static IObservable<Unit> ProcessContext(this IKayakContext context, IKayakFrameworkBehavior behavior)
        {
            return context.ProcessContextInternal(behavior).AsCoroutine<Unit>();
        }

        static IEnumerable<object> ProcessContextInternal(this IKayakContext context, IKayakFrameworkBehavior behavior)
        {
            yield return context.Request.Begin();

            var info = new InvocationInfo();
            context.SetInvocationInfo(info);

            yield return behavior.Route(context);

            var failed = false;
            var auth = behavior.Authenticate(context);

            if (auth != null)
                yield return auth.Do(r => failed = r);

            if (!failed)
            {
                info.Target = Activator.CreateInstance(info.Method.DeclaringType);

                var parameterCount = info.Method.GetParameters().Length;
                info.Arguments = new object[parameterCount];

                yield return behavior.Bind(context);

                context.PerformInvocation();

                yield return behavior.Handle(context);
            }

            yield return context.Response.End();
        }

        public static IHttpResponder CreateFramework(MethodMap methodMap, TypedJsonMapper mapper)
        {
            return new KayakFrameworkResponder2(methodMap, mapper);
        }
    }

    public class KayakFrameworkResponder2 : IHttpResponder
    {
        MethodMap methodMap;
        TypedJsonMapper mapper;

        public KayakFrameworkResponder2(MethodMap methodMap, TypedJsonMapper mapper)
        {
            this.methodMap = methodMap;
            this.mapper = mapper;
        }

        public IObservable<IHttpServerResponse> Respond(IHttpServerRequest request, IDictionary<object, object> context)
        {
            return RespondInternal(request, context).AsCoroutine<IHttpServerResponse>();
        }

        public IEnumerable<object> RespondInternal(IHttpServerRequest request, IDictionary<object, object> context)
        {
            var info = new InvocationInfo();

            info.Method = methodMap.GetMethod(request.GetPath(), request.RequestLine.Verb, context);
            info.Target = Activator.CreateInstance(info.Method.DeclaringType);
            info.Arguments = new object[info.Method.GetParameters().Length];

            context.SetInvocationInfo(info);

            IDictionary<string, string> target = new Dictionary<string, string>();

            var pathParams = context.GetPathParameters();
            var queryString = request.GetQueryString();

            target.Concat(pathParams, queryString);

            info.BindNamedParameters(target, context.Coerce);

            yield return info.DeserializeArgsFromJson(request, mapper);

            var service = info.Target as KayakService2;

            if (service != null)
            {
                service.Context = context;
                service.Request = request;
            }

            info.Invoke();

            if (info.Result is IHttpServerResponse)
                yield return info.Result;


        }
    }


    public interface IKayakFrameworkBehavior2
    {
        IObservable<IHttpServerResponse> Route(IHttpServerRequest request, IDictionary<object, object> context);
        IObservable<IHttpServerResponse> Authenticate(IHttpServerRequest request, IDictionary<object, object> context);
        IObservable<Unit> Bind(IHttpServerRequest request, IDictionary<object, object> context);
        IObservable<IHttpServerResponse> GetResponse(IHttpServerRequest request, IDictionary<object, object> context);
    }

    public class KayakFrameworkResponder : IHttpResponder
    {
        IKayakFrameworkBehavior2 behavior;
        public KayakFrameworkResponder(IKayakFrameworkBehavior2 behavior)
        {
            this.behavior = behavior;
        }

        public IObservable<IHttpServerResponse> Respond(IHttpServerRequest request, IDictionary<object, object> context)
        {
            return RespondInternal(request, context).AsCoroutine<IHttpServerResponse>();
        }

        IEnumerable<object> RespondInternal(IHttpServerRequest request, IDictionary<object, object> context)
        {
            var info = new InvocationInfo();
            context.SetInvocationInfo(info);

            IHttpServerResponse response = null;

            yield return behavior.Route(request, context).Do(r => response = r);

            var auth = behavior.Authenticate(request, context);

            if (auth != null)
                yield return auth.Do(r => response = r);

            if (response == null)
            {
                info.Target = Activator.CreateInstance(info.Method.DeclaringType);

                var parameterCount = info.Method.GetParameters().Length;
                info.Arguments = new object[parameterCount];

                yield return behavior.Bind(request, context);

                var service = info.Target as KayakService2;

                if (service != null)
                {
                    service.Context = context;
                    service.Request = request;
                }

                info.Invoke();

                if (info.Result is IHttpServerResponse)
                    response = info.Result as IHttpServerResponse;
            }

            if (response == null)
                yield return behavior.GetResponse(request, context).Do(r => response = r);

            yield return response;
        }
    }

    public class BaseResponse : IHttpServerResponse
    {
        public HttpStatusLine StatusLine { get; set; }
        Dictionary<string, string> headers;

        public IDictionary<string, string> Headers
        {
            get { return headers ?? (headers = new Dictionary<string, string>()); }
        }

        public string BodyFile { get; set; }

        public virtual IObservable<ArraySegment<byte>> GetBodyChunk()
        {
            return null;
        }
    }

    public class BufferedResponse : BaseResponse
    {
        LinkedList<ArraySegment<byte>> buffers;

        public void SetContentLength()
        {
            var cl = 0;
            foreach (var b in buffers)
                cl += b.Count;
            if (cl > 0)
                Headers.SetContentLength(cl);
        }
        public void Add(ArraySegment<byte> buffer)
        {
            if (buffers == null)
                buffers = new LinkedList<ArraySegment<byte>>();

            buffers.AddLast(buffer);
        }

        public void Add(byte[] buffer)
        {
            Add(new ArraySegment<byte>(buffer));
        }

        public void Add(string s)
        {
            Add(new ArraySegment<byte>(Encoding.UTF8.GetBytes(s)));
        }

        public override IObservable<ArraySegment<byte>> GetBodyChunk()
        {
            if (buffers == null)
                return null;

            var b = buffers.First;
            buffers.RemoveFirst();

            if (buffers.Count == 0)
                buffers = null;

            return new ArraySegment<byte>[] { b.Value }.ToObservable();
        }
    }
}
