Blazor.start({
    configureSignalR: builder => builder.withServerTimeout(120000).withKeepAliveInterval(10000)
});
