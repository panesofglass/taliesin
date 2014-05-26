namespace Taliesin

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Dyfrig

[<AutoOpen>]
module Extensions =
    type Microsoft.FSharp.Control.Async with
        static member AwaitTask(task: Task) =
            Async.AwaitTask(task.ContinueWith(Func<_,_>(fun _ -> ())))

module Dyfrig =
    
    // TODO: Update Dyfrig dependency to get this directly from env.GetRequestUri()
    type Environment with
        member env.GetBaseUri() =
            env.RequestScheme + "://" +
            (env.RequestHeaders.["Host"].[0]) +
            if String.IsNullOrEmpty env.RequestPathBase then "/" else env.RequestPathBase

        member env.GetRequestUri() =
            env.RequestScheme + "://" +
            (env.RequestHeaders.["Host"].[0]) +
            env.RequestPathBase +
            env.RequestPath +
            if String.IsNullOrEmpty env.RequestQueryString then "" else "?" + env.RequestQueryString


type OwinEnv = IDictionary<string, obj>
type OwinAppFunc = Func<OwinEnv, Task>

type HttpMethod =
    | GET
    | HEAD
    | POST
    | PUT
    | PATCH
    | DELETE
    | TRACE
    | OPTIONS
    | Custom of string
    with
    override x.ToString() =
        match x with
        | GET     _ -> "GET"
        | HEAD    _ -> "HEAD"
        | POST    _ -> "POST"
        | PUT     _ -> "PUT"
        | PATCH   _ -> "PATCH"
        | DELETE  _ -> "DELETE"
        | TRACE   _ -> "TRACE"
        | OPTIONS _ -> "OPTIONS"
        | Custom(m) -> m

/// Message type that associates an `HttpApplication with an HTTP method.
type internal HttpMethodHandler = HttpMethodHandler of HttpMethod * OwinAppFunc

/// Alias `MailboxProcessor<'T>` as `Agent<'T>`.
type Agent<'T> = MailboxProcessor<'T>

/// Messages used by the HTTP resource agent.
type internal ResourceMessage =
    | Request of OwinEnv * AsyncReplyChannel<unit>
    | SetHandler of HttpMethod * OwinAppFunc
    | Shutdown

/// An HTTP resource agent.
type Resource(uriTemplate, allowedMethods: HttpMethod list, methodNotAllowedHandler) =
    let onError = new Event<exn>()
    let onExecuting = new Event<OwinEnv>()
    let onExecuted = new Event<OwinEnv>()
    let agent = Agent<ResourceMessage>.Start(fun inbox ->
        let rec loop allowedMethods (handlers: HttpMethodHandler list) = async {
            let! msg = inbox.Receive()
            match msg with
            | Request(env, channel) ->
                let env = Environment.toEnvironment env
                let owinEnv = env :> OwinEnv 
                let foundHandler =
                    handlers
                    |> List.tryFind (fun (HttpMethodHandler(m, _)) -> m.ToString() = env.RequestMethod)
                let selectedHandler =
                    match foundHandler with
                    | Some(HttpMethodHandler(_, h)) -> h
                    | None -> methodNotAllowedHandler allowedMethods
                onExecuting.Trigger owinEnv
                let task = selectedHandler.Invoke owinEnv
                do! task |> Async.AwaitTask
                onExecuted.Trigger owinEnv
                // TODO: Should return whether the task succeeded or failed.
                channel.Reply()
                return! loop allowedMethods handlers
            | SetHandler(httpMethod, handler) ->
                let foundMethod = allowedMethods |> List.tryFind ((=) httpMethod)
                let handlers' =
                    match foundMethod with
                    | Some _ ->
                        let handler = HttpMethodHandler(httpMethod, handler)
                        let otherHandlers = 
                            handlers |> List.filter (fun (HttpMethodHandler(m,_)) -> m <> httpMethod)
                        handler :: otherHandlers
                    | None -> handlers
                return! loop allowedMethods handlers'
            | Shutdown -> ()
        }
            
        loop allowedMethods []
    )

    /// The URI template for this `Resource`.
    member x.UriTemplate = uriTemplate

    /// Invokes the `Resource` and asynchronously waits for the completion.
    member x.AsyncInvoke(env) =
        agent.PostAndAsyncReply(fun channel -> Request(env, channel))

    /// Invokes the `Resource` as an OWIN handler.
    member x.Invoke : OwinAppFunc =
        Func<_,_>(fun env -> x.AsyncInvoke env |> Async.StartAsTask :> Task)

    /// Sets the handler for the specified `HttpMethod`.
    /// Ideally, we would expose methods matching the allowed methods.
    member x.SetHandler(handler) =
        agent.Post <| SetHandler(handler)

    /// Stops the resource agent.
    member x.Shutdown() = agent.Post Shutdown

    /// Provide stream of `exn` for logging purposes.
    [<CLIEvent>]
    member x.Error = onError.Publish
    /// Provide stream of environments before executing the request handler.
    [<CLIEvent>]
    member x.Executing = onExecuting.Publish
    /// Provide stream of environments after the request handler is executed.
    [<CLIEvent>]
    member x.Executed = onExecuted.Publish


/// Type alias for URI templates
type UriRouteTemplate = string

/// Defines the route for a specific resource
type RouteDef<'TName> = 'TName * UriRouteTemplate * HttpMethod list

/// Defines the tree type for specifying resource routes
/// Example:
///     type Routes = Root | About | Customers | Customer
///     let spec =
///         RouteNode((Home, "", [GET]),
///         [
///             RouteLeaf(About, "about", [GET])
///             RouteNode((Customers, "customers", [GET; POST]),
///             [
///                 RouteLeaf(Customer, "{id}", [GET; PUT; DELETE])
///             ])
///         ])            
type RouteSpec<'TRoute> =
    | RouteLeaf of RouteDef<'TRoute>
    | RouteNode of RouteDef<'TRoute> * RouteSpec<'TRoute> list

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RouteSpec =
    let tryFind requestUri =
        // TODO: implement n-ary tree walker
        // TODO: break apart pieces of the URI as each node of the tree is found.
        // TODO: parse and match types specified in URI templates
        ()

/// Default implementations of the 405 handler and URI matcher
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal ResourceManager =
    open Dyfrig

    /// Default 405 Method Not Allowed OWIN handler
    let notAllowed (allowedMethods: HttpMethod list) = Func<_,_>(fun env ->
        let env = Environment.toEnvironment env
        env.ResponseStatusCode <- 405
        let bytes =
            allowedMethods
            |> List.fold (fun a b -> a + " " + b.ToString()) ""
            |> sprintf "405 Method Not Allowed. Try one of %s"
            |> System.Text.Encoding.ASCII.GetBytes
        async {
            do! env.ResponseBody.AsyncWrite(bytes)
        } |> Async.StartAsTask :> Task)

    /// Default URI matching algorithm
    let uriMatcher uriTemplate env =
        let env = Environment.toEnvironment env
        // TODO: Do this with F# rather than System.ServiceModel. This could easily use a Regex pattern.
        let template = UriTemplate(uriTemplate)
        let baseUri = Uri(env.GetBaseUri())
        let requestUri = Uri(env.GetRequestUri())
        let result = template.Match(baseUri, requestUri)
        // TODO: Return the match result as well as true/false, as we can retrieve parameter values this way.
        if result = null then false else
        // TODO: Investigate ways to avoid mutation.
        env.Add("taliesin.UriTemplateMatch", result) |> ignore
        true

    /// Helper function to concatenate URI templates during `ResourceManager` construction.
    let concatUriTemplate baseTemplate template =
        if String.IsNullOrEmpty baseTemplate then template else baseTemplate + "/" + template

    /// Helper function to append a new `Resource` to the list of managed resources.
    let addResource resources name uriTemplate allowedMethods notAllowed =
        let resource = new Resource(uriTemplate, allowedMethods, notAllowed)
        (name, resource) :: resources

    /// Flattens the provided `RouteSpec` into a list of named `Resource`s.
    let rec addResources uriTemplate notAllowed resources = function
        | RouteNode((name, template, httpMethods), nestedRoutes) ->
            let uriTemplate' = concatUriTemplate uriTemplate template
            let resources' = addResource resources name uriTemplate' httpMethods notAllowed
            addNestedResources uriTemplate' notAllowed resources' nestedRoutes
        | RouteLeaf(name, template, httpMethods) ->
            let uriTemplate' = concatUriTemplate uriTemplate template
            addResource resources name uriTemplate' httpMethods notAllowed

    /// Flattens nested members in the provided `RouteSpec`.
    and addNestedResources uriTemplate notAllowed resources routes =
        match routes with
        | [] -> resources
        | route::routes ->
            let resources' = addResources uriTemplate notAllowed resources route
            match routes with
            | [] -> resources'
            | _ -> addNestedResources uriTemplate notAllowed resources' routes


/// Manages traffic flow within the application to specific routes.
/// Connect resource handlers using:
///     let app = ResourceManager<HttpRequestMessage, HttpResponseMessage, Routes>(spec)
///     app.[Root].SetHandler(GET, (fun request -> async { return response }))
/// A type provider could make this much nicer, e.g.:
///     let app = ResourceManager<"path/to/spec/as/string">
///     app.Root.Get(fun request -> async { return response })
type ResourceManager<'TRoute when 'TRoute : equality>(routeSpec, ?uriMatcher, ?methodNotAllowedHandler) as x =
    // Should this also be an Agent<'T>?
    inherit Dictionary<'TRoute, Resource>(HashIdentity.Structural)

    let uriMatcher = defaultArg uriMatcher ResourceManager.uriMatcher
    let methodNotAllowedHandler = defaultArg methodNotAllowedHandler ResourceManager.notAllowed

    let onError = new Event<exn>()
    let onExecuting = new Event<OwinEnv>()
    let onExecuted = new Event<OwinEnv>()

    // TODO: This should probably manage a supervising agent of its own.
    let resources = ResourceManager.addResources "" methodNotAllowedHandler [] routeSpec
    do for name, resource in resources do x.Add(name, resource)

    [<CLIEvent>]
    member x.Error = onError.Publish
    [<CLIEvent>]
    member x.Executing = onExecuting.Publish
    [<CLIEvent>]
    member x.Executed = onExecuted.Publish

    /// Asynchronously invokes the `ResourceManager` with the specified `OwinEnv`.
    member x.AsyncInvoke(env: OwinEnv) = async {
        let env = Environment.toEnvironment env
        // TODO: Switch to RouteSpec.tryFind to traverse RouteSpec to find matching resource
        let foundResource =
            resources |> List.tryFind (fun (_, r) -> uriMatcher r.UriTemplate env)
        match foundResource with
        | Some (_, resource) ->
            do! resource.AsyncInvoke env
        | None ->
            env.ResponseStatusCode <- 404
    }

    /// Invokes the `ResourceManager` as an OWIN application.
    member x.Invoke : OwinAppFunc =
        Func<_,_>(fun env -> x.AsyncInvoke env |> Async.StartAsTask :> Task)

    interface IDisposable with
        /// Shutdown all resource agents.
        member x.Dispose() = for resource in x.Values do resource.Shutdown()
