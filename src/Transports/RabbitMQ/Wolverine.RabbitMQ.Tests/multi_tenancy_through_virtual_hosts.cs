using System.Diagnostics;
using System.Net;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute.ReceivedExtensions;
using Oakton.Resources;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public record MultiTenantMessage(Guid Id);
public record MultiTenantResponse(Guid Id);

public static class MultiTenantMessageHandler
{
    public static object Handle(MultiTenantMessage message)
    {
        return new MultiTenantResponse(message.Id).ToDestination("rabbitmq://queue/multi_response".ToUri());
    }

    public static void Handle(MultiTenantResponse message) => Debug.WriteLine("Got a response");
}

public class MultiTenantedRabbitFixture : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await declareVirtualHost("vh1");
        await declareVirtualHost("vh2");
        await declareVirtualHost("vh3");

        Main = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                
                opts.ServiceName = "main";
                
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup()
                    .AddTenant("one", "vh1")
                    .AddTenant("two", "vh2")
                    .AddTenant("three", "vh3");

                // Listen for multiples
                opts.ListenToRabbitQueue("multi_response");

                opts.PublishMessage<MultiTenantMessage>().ToRabbitQueue("multi_incoming");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        One = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "one";
                opts.UseRabbitMq(f => f.VirtualHost = "vh1");
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
                
                
            }).StartAsync();
        
        Two = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "two";
                opts.UseRabbitMq(f => f.VirtualHost = "vh2");
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
        
        Three = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "three";
                opts.UseRabbitMq(f => f.VirtualHost = "vh3");
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public IHost Three { get; set; }

    public IHost Two { get; set; }

    public IHost One { get; set; }

    public IHost Main { get; private set; }

    public async Task DisposeAsync()
    {
        await Main.StopAsync();
        await One.StopAsync();
        await Two.StopAsync();
        await Three.StopAsync();
    }
    
    private static async Task<HttpResponseMessage> declareVirtualHost(string vhname)
    {
        var credentials = new NetworkCredential("guest", "guest");
        using var handler = new HttpClientHandler { Credentials = credentials };
        using var client = new HttpClient(handler);
        

        var request = new HttpRequestMessage(HttpMethod.Put, $"http://localhost:15672/api/vhosts/{vhname}");
        

        var response = await client.SendAsync(request);
        return response;
    }
}

public class multi_tenancy_through_virtual_hosts : IClassFixture<MultiTenantedRabbitFixture>
{
    private readonly MultiTenantedRabbitFixture _fixture;

    public multi_tenancy_through_virtual_hosts(MultiTenantedRabbitFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task send_message_to_a_specific_tenant()
    {
        var message = new MultiTenantMessage(Guid.NewGuid());
        var session = await _fixture.Main
            .TrackActivity()
            .AlsoTrack(_fixture.One, _fixture.Two, _fixture.Three)
            .WaitForMessageToBeReceivedAt<MultiTenantMessage>(_fixture.Two)
            .SendMessageAndWaitAsync(message, new DeliveryOptions{TenantId = "two"});

        var record = session.Received.SingleRecord<MultiTenantMessage>();
        record.ServiceName.ShouldBe("two");

        var response = session.Received.SingleRecord<MultiTenantResponse>();
        response.ServiceName.ShouldBe("main");
        
        // Label the envelope as tenant id = "two" because it was received at that point
        response.Envelope.TenantId.ShouldBe("two");
        response.Envelope.Message.ShouldBeOfType<MultiTenantResponse>()
            .Id.ShouldBe(message.Id);
    }
}

public static class MultiTenantedRabbitMqSamples
{
    public static async Task Configure()
    {
        #region sample_configuring_rabbit_mq_for_tenancy

        var builder = Host.CreateApplicationBuilder();

        builder.UseWolverine(opts =>
        {
            // At this point, you still have to have a *default* broker connection to be used for 
            // messaging. 
            opts.UseRabbitMq(new Uri(builder.Configuration.GetConnectionString("main")))
                
                // This will be respected across *all* the tenant specific
                // virtual hosts and separate broker connections
                .AutoProvision()

                // This is the default, if there is no tenant id on an outgoing message,
                // use the default broker
                .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

                // Or tell Wolverine instead to just quietly ignore messages sent
                // to unrecognized tenant ids
                .TenantIdBehavior(TenantedIdBehavior.IgnoreUnknownTenants)

                // Or be draconian and make Wolverine assert and throw an exception
                // if an outgoing message does not have a tenant id
                .TenantIdBehavior(TenantedIdBehavior.TenantIdRequired)

                // Add specific tenants for separate virtual host names
                // on the same broker as the default connection
                .AddTenant("one", "vh1")
                .AddTenant("two", "vh2")
                .AddTenant("three", "vh3")

                // Or, you can add a broker connection to something completel
                // different for a tenant
                .AddTenant("four", new Uri(builder.Configuration.GetConnectionString("rabbit_four")));

            // This Wolverine application would be listening to a queue
            // named "incoming" on all virtual hosts and/or tenant specific message
            // brokers
            opts.ListenToRabbitQueue("incoming");

            // More on this in the docs....
            opts.PublishMessage<Message1>()
                .ToRabbitQueue("outgoing");
        });

        #endregion
        
        
    }

    #region sample_send_message_to_specific_tenant

    public static async Task send_message_to_specific_tenant(IMessageBus bus)
    {
        // Send a message tagged to a specific tenant id
        await bus.PublishAsync(new Message1(), new DeliveryOptions { TenantId = "two" });
    }

    #endregion
}