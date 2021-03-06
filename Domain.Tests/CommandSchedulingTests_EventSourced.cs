// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using FluentAssertions;
using Its.Log.Instrumentation;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Microsoft.Its.Domain.Testing;
using Microsoft.Its.Recipes;
using NUnit.Framework;
using Sample.Domain;
using Sample.Domain.Ordering;
using Sample.Domain.Ordering.Commands;

namespace Microsoft.Its.Domain.Tests
{
    [Category("Command scheduling")]
    [TestFixture]
    public class CommandSchedulingTests_EventSourced
    {
        private CompositeDisposable disposables;
        private Configuration configuration;
        private Guid customerAccountId;
        private IEventSourcedRepository<CustomerAccount> customerRepository;
        private IEventSourcedRepository<Order> orderRepository;

        [SetUp]
        public void SetUp()
        {
            disposables = new CompositeDisposable();
            // disable authorization
            Command<Order>.AuthorizeDefault = (o, c) => true;
            Command<CustomerAccount>.AuthorizeDefault = (o, c) => true;

            disposables.Add(VirtualClock.Start());

            customerAccountId = Any.Guid();

            configuration = new Configuration()
                .UseInMemoryCommandScheduling()
                .UseInMemoryEventStore()
                .TraceScheduledCommands();

            customerRepository = configuration.Repository<CustomerAccount>();
            orderRepository = configuration.Repository<Order>();

            customerRepository.Save(new CustomerAccount(customerAccountId)
                                        .Apply(new ChangeEmailAddress(Any.Email())));

            disposables.Add(ConfigurationContext.Establish(configuration));
            disposables.Add(configuration);
        }

        [TearDown]
        public void TearDown()
        {
            disposables.Dispose();
        }

        [Test]
        public void When_a_command_is_scheduled_for_later_execution_then_a_CommandScheduled_event_is_added()
        {
            var order = CreateOrder();

            order.Apply(new ShipOn(shipDate: Clock.Now().AddMonths(1).Date));

            var lastEvent = order.PendingEvents.Last();
            lastEvent.Should().BeOfType<CommandScheduled<Order>>();
            lastEvent.As<CommandScheduled<Order>>().Command.Should().BeOfType<Ship>();
        }

        [Test]
        public async Task CommandScheduler_executes_scheduled_commands_immediately_if_no_due_time_is_specified()
        {
            // arrange
            var repository = configuration.Repository<Order>();

            var order = CreateOrder();

            // act
            order.Apply(new ShipOn(Clock.Now().Subtract(TimeSpan.FromDays(2))));
            await repository.Save(order);

            //assert 
            order = await repository.GetLatest(order.Id);
            var lastEvent = order.Events().Last();
            lastEvent.Should().BeOfType<Order.Shipped>();
        }

        [Test]
        public async Task When_a_scheduled_command_fails_validation_then_a_failure_event_can_be_recorded_in_HandleScheduledCommandException_method()
        {
            // arrange
            var order = CreateOrder(customerAccountId: (await customerRepository.GetLatest(customerAccountId)).Id);

            // by the time Ship is applied, it will fail because of the cancellation
            order.Apply(new ShipOn(shipDate: Clock.Now().AddMonths(1).Date));
            order.Apply(new Cancel());
            await orderRepository.Save(order);

            // act
            VirtualClock.Current.AdvanceBy(TimeSpan.FromDays(32));

            //assert 
            order = await orderRepository.GetLatest(order.Id);
            var lastEvent = order.Events().Last();
            lastEvent.Should().BeOfType<Order.ShipmentCancelled>();
        }

        [Test]
        public async Task When_applying_a_scheduled_command_throws_then_further_command_scheduling_is_not_interrupted()
        {
            // arrange
            var order1 = CreateOrder(customerAccountId: customerAccountId)
                .Apply(new ShipOn(shipDate: Clock.Now().AddMonths(1).Date))
                .Apply(new Cancel());
            await orderRepository.Save(order1);
            var order2 = CreateOrder(customerAccountId: customerAccountId)
                .Apply(new ShipOn(shipDate: Clock.Now().AddMonths(1).Date));
            await orderRepository.Save(order2);

            // act
            VirtualClock.Current.AdvanceBy(TimeSpan.FromDays(32));

            // assert 
            order1 = await orderRepository.GetLatest(order1.Id);
            var lastEvent = order1.Events().Last();
            lastEvent.Should().BeOfType<Order.ShipmentCancelled>();

            order2 = await orderRepository.GetLatest(order2.Id);
            lastEvent = order2.Events().Last();
            lastEvent.Should().BeOfType<Order.Shipped>();
        }

        [Test]
        public async Task A_command_can_be_scheduled_against_another_aggregate()
        {
            var order = new Order(
                new CreateOrder(Any.FullName())
                {
                    CustomerId = customerAccountId
                })
                .Apply(new AddItem
                {
                    ProductName = Any.Word(),
                    Price = 12.99m
                })
                .Apply(new Cancel());
            await orderRepository.Save(order);

            var customerAccount = await customerRepository.GetLatest(customerAccountId);

            customerAccount.Events()
                           .Last()
                           .Should()
                           .BeOfType<CustomerAccount.OrderCancelationConfirmationEmailSent>();
        }

        [Test]
        public void If_Schedule_is_dependent_on_an_event_with_no_aggregate_id_then_it_throws()
        {
            var scheduler = configuration.CommandScheduler<CustomerAccount>();

            Action schedule = () => scheduler.Schedule(
                Any.Guid(),
                new SendOrderConfirmationEmail(Any.Word()),
                deliveryDependsOn: new Order.Created
                {
                    AggregateId = Guid.Empty,
                    ETag = Any.Word()
                }).Wait();

            schedule.ShouldThrow<ArgumentException>()
                    .And
                    .Message
                    .Should()
                    .Contain("An AggregateId must be set on the event on which the scheduled command depends.");
        }

        [Test]
        public async Task If_Schedule_is_dependent_on_an_event_with_no_ETag_then_it_sets_one()
        {
            var scheduler = new Configuration()
                .UseInMemoryEventStore()
                .UseInMemoryCommandScheduling()
                .CommandScheduler<CustomerAccount>();

            var created = new Order.Created
            {
                AggregateId = Any.Guid(),
                ETag = null
            };

            await scheduler.Schedule(
                Any.Guid(),
                new SendOrderConfirmationEmail(Any.Word()),
                deliveryDependsOn: created);

            created.ETag.Should().NotBeNullOrEmpty();
        }

        [Test]
        public async Task Scheduled_commands_triggered_by_a_scheduled_command_are_idempotent()
        {
            var aggregate = new CommandSchedulerTestAggregate();
            var repository = configuration.Repository<CommandSchedulerTestAggregate>();

            await repository.Save(aggregate);

            var scheduler = configuration.CommandScheduler<CommandSchedulerTestAggregate>();

            var dueTime = Clock.Now().AddMinutes(5);

            Console.WriteLine(new { dueTime });

            var command = new CommandSchedulerTestAggregate.CommandThatSchedulesTwoOtherCommandsImmediately
            {
                NextCommand1AggregateId = aggregate.Id,
                NextCommand1 = new CommandSchedulerTestAggregate.Command
                {
                    CommandId = Any.CamelCaseName()
                },
                NextCommand2AggregateId = aggregate.Id,
                NextCommand2 = new CommandSchedulerTestAggregate.Command
                {
                    CommandId = Any.CamelCaseName()
                }
            };

            await scheduler.Schedule(
                aggregate.Id,
                dueTime: dueTime,
                command: command);
            await scheduler.Schedule(
                aggregate.Id,
                dueTime: dueTime,
                command: command);

            VirtualClock.Current.AdvanceBy(TimeSpan.FromDays(1));

            aggregate = await repository.GetLatest(aggregate.Id);

            var events = aggregate.Events().ToArray();
            events.Count().Should().Be(3);
            var succeededEvents = events.OfType<CommandSchedulerTestAggregate.CommandSucceeded>().ToArray();
            succeededEvents.Count().Should().Be(2);
            succeededEvents.First().Command.CommandId
                           .Should().NotBe(succeededEvents.Last().Command.CommandId);
        }

        [Test]
        public async Task Scatter_gather_produces_a_unique_etag_per_sent_command()
        {
            var repo = configuration.Repository<MarcoPoloPlayerWhoIsIt>();
            var it = new MarcoPoloPlayerWhoIsIt();
            await repo.Save(it);

            var numberOfPlayers = 6;
            var players = Enumerable.Range(1, numberOfPlayers)
                                    .Select(_ => new MarcoPoloPlayerWhoIsNotIt());

            foreach (var player in players)
            {
                var joinGame = new MarcoPoloPlayerWhoIsNotIt.JoinGame
                {
                    IdOfPlayerWhoIsIt = it.Id
                };
                await player.ApplyAsync(joinGame).AndSave();
            }

            await repo.Refresh(it);

            await it.ApplyAsync(new MarcoPoloPlayerWhoIsIt.SayMarco()).AndSave();

            await repo.Refresh(it);

            it.Events()
              .OfType<MarcoPoloPlayerWhoIsIt.HeardPolo>()
              .Count()
              .Should()
              .Be(numberOfPlayers);
        }

        [Test]
        public async Task Multiple_scheduled_commands_having_the_some_causative_command_etag_have_repeatable_and_unique_etags()
        {
            var scheduled = new List<ICommand>();

            configuration.AddToCommandSchedulerPipeline<MarcoPoloPlayerWhoIsIt>(async (cmd, next) =>
            {
                scheduled.Add(cmd.Command);
                await next(cmd);
            });
            configuration.AddToCommandSchedulerPipeline<MarcoPoloPlayerWhoIsNotIt>(async (cmd, next) =>
            {
                scheduled.Add(cmd.Command);
                await next(cmd);
            });

            var it = new MarcoPoloPlayerWhoIsIt()
                .Apply(new MarcoPoloPlayerWhoIsIt.AddPlayer { PlayerId = Any.Guid() })
                .Apply(new MarcoPoloPlayerWhoIsIt.AddPlayer { PlayerId = Any.Guid() });
            Console.WriteLine("[Saving]");
            await configuration.Repository<MarcoPoloPlayerWhoIsIt>().Save(it);

            var sourceEtag = Any.Guid().ToString();

            await it.ApplyAsync(new MarcoPoloPlayerWhoIsIt.KeepSayingMarcoOverAndOver
            {
                ETag = sourceEtag
            });
            var firstPassEtags = scheduled.Select(c => c.ETag).ToArray();
            Console.WriteLine(new { firstPassEtags }.ToLogString());

            scheduled.Clear();

            // revert the aggregate and do the same thing again
            it = await configuration.Repository<MarcoPoloPlayerWhoIsIt>().GetLatest(it.Id);
            await it.ApplyAsync(new MarcoPoloPlayerWhoIsIt.KeepSayingMarcoOverAndOver
            {
                ETag = sourceEtag
            });

            Console.WriteLine("about to advance clock for the second time");

            var secondPassEtags = scheduled.Select(c => c.ETag).ToArray();
            Console.WriteLine(new { secondPassEtags }.ToLogString());

            secondPassEtags.Should()
                           .Equal(firstPassEtags);
        }

        [Test]
        public async Task Aggregates_can_schedule_commands_against_themselves_idempotently()
        {
            var it = new MarcoPoloPlayerWhoIsIt();
            await configuration.Repository<MarcoPoloPlayerWhoIsIt>().Save(it);

            await it.ApplyAsync(new MarcoPoloPlayerWhoIsIt.KeepSayingMarcoOverAndOver());

            VirtualClock.Current.AdvanceBy(TimeSpan.FromMinutes(1));

            await configuration.Repository<MarcoPoloPlayerWhoIsIt>().Refresh(it);

            it.Events()
              .OfType<MarcoPoloPlayerWhoIsIt.SaidMarco>()
              .Count()
              .Should()
              .BeGreaterOrEqualTo(5);
        }

        public static Order CreateOrder(
            DateTimeOffset? deliveryBy = null,
            string customerName = null,
            Guid? orderId = null,
            Guid? customerAccountId = null)
        {
            return new Order(
                new CreateOrder(customerName ?? Any.FullName())
                {
                    AggregateId = orderId ?? Any.Guid(),
                    CustomerId = customerAccountId ?? Any.Guid()
                })
                .Apply(new AddItem
                {
                    Price = 499.99m,
                    ProductName = Any.Words(1, true).Single()
                })
                .Apply(new SpecifyShippingInfo
                {
                    Address = Any.Words(1, true).Single() + " St.",
                    City = "Seattle",
                    StateOrProvince = "WA",
                    Country = "USA",
                    DeliverBy = deliveryBy
                });
        }
    }
}