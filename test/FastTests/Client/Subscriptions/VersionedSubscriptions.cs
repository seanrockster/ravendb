﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Server.Versioning;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Json;
using Xunit;
using Sparrow;

namespace FastTests.Client.Subscriptions
{
    public class VersionedSubscriptions:RavenTestBase
    {
        private readonly TimeSpan _reasonableWaitTime = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15);
        [Fact]
        public async Task PlainVersionedSubscriptions()
        {
            using (var store = GetDocumentStore())
            {

                var subscriptionId = await store.AsyncSubscriptions.CreateAsync(new SubscriptionCreationOptions<Versioned<User>>());

                using (var context = JsonOperationContext.ShortTermSingleUse())
                {
                    var versioningDoc = new VersioningConfiguration
                    {
                        Default = new VersioningConfigurationCollection
                        {
                            Active = true,
                            MinimumRevisionsToKeep = 5,
                        },
                        Collections = new Dictionary<string, VersioningConfigurationCollection>
                        {
                            ["Users"] = new VersioningConfigurationCollection
                            {
                                Active = true
                            },
                            ["Dons"] = new VersioningConfigurationCollection
                            {
                                Active = true,
                            }
                        }
                    };

                    await Server.ServerStore.ModifyDatabaseVersioning(context,
                        store.Database,
                        EntityToBlittable.ConvertEntityToBlittable(versioningDoc,
                            new DocumentConventions(),
                            context));
                }

                for (int i = 0; i < 10; i++)
                {
                    for (var j = 0; j < 10; j++)
                    {
                        using (var session = store.OpenSession())
                        {
                            session.Store(new User
                            {
                                Name = $"users{i} ver {j}"
                            }, "users/" + i);

                            session.Store(new Company()
                            {
                                Name = $"dons{i} ver {j}"
                            }, "dons/" + i);

                            session.SaveChanges();
                        }
                    }
                }

                using (var sub = store.Subscriptions.Open<Versioned<User>>(new SubscriptionConnectionOptions(subscriptionId)))
                {
                    var mre = new AsyncManualResetEvent();
                    var names = new HashSet<string>();
                    sub.Subscribe(x =>
                    {
                        names.Add(x.Current?.Name + x.Previous?.Name);
                        if (names.Count == 100)
                            mre.Set();
                    });
                    sub.Start();

                    Assert.True(await mre.WaitAsync(_reasonableWaitTime));

                }
            }
        }

        [Fact]
        public async Task PlainVersionedSubscriptionsCompareDocs()
        {
            using (var store = GetDocumentStore())
            {

                var subscriptionId = await store.AsyncSubscriptions.CreateAsync(new SubscriptionCreationOptions<Versioned<User>>());

                using (var context = JsonOperationContext.ShortTermSingleUse())
                {
                    var versioningDoc = new VersioningConfiguration
                    {
                        Default = new VersioningConfigurationCollection
                        {
                            Active = true,
                            MinimumRevisionsToKeep = 5,
                        },
                        Collections = new Dictionary<string, VersioningConfigurationCollection>
                        {
                            ["Users"] = new VersioningConfigurationCollection
                            {
                                Active = true
                            },
                            ["Dons"] = new VersioningConfigurationCollection
                            {
                                Active = true,
                            }
                        }
                    };

                    await Server.ServerStore.ModifyDatabaseVersioning(context,
                        store.Database,
                        EntityToBlittable.ConvertEntityToBlittable(versioningDoc,
                            new DocumentConventions(),
                            context));
                }

                for (var j = 0; j < 10; j++)
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User
                        {
                            Name = $"users1 ver {j}",
                            Age = j
                        }, "users/1");

                        session.Store(new Company()
                        {
                            Name = $"dons1 ver {j}"
                        }, "dons/1");

                        session.SaveChanges();
                    }
                }

                using (var sub = store.Subscriptions.Open<Versioned<User>>(new SubscriptionConnectionOptions(subscriptionId)))
                {
                    var mre = new AsyncManualResetEvent();
                    var names = new HashSet<string>();
                    var maxAge = -1;
                    sub.Subscribe(x =>
                    {
                        if (x.Current.Age  > maxAge && x.Current.Age > (x.Previous?.Age ?? -1))
                        {
                            names.Add(x.Current?.Name + x.Previous?.Name);
                            maxAge = x.Current.Age;
                        }
                        names.Add(x.Current?.Name + x.Previous?.Name);
                        if (names.Count == 10)
                            mre.Set();
                    });
                    sub.Start();

                    Assert.True(await mre.WaitAsync(_reasonableWaitTime));

                }
            }
        }

        public class Result
        {
            public string Id;
            public int Age;
        }

        [Fact]
        public async Task VersionedSubscriptionsWithCustomScript()
        {
            using (var store = GetDocumentStore())
            {

                var subscriptionId = await store.AsyncSubscriptions.CreateAsync(new SubscriptionCreationOptions<User>
                {
                    Criteria = new SubscriptionCriteria<User>
                    {
                        Script = @"
                        if(!!this.Current && !!this.Previous && this.Current.Age > this.Previous.Age)
                        {
                            return { Id: this.Current[""@metadata""][""@id""], Age: this.Current.Age }
                        }
                        else return false;
                        ",
                        IsVersioned = true
                    }
                });

                using (var context = JsonOperationContext.ShortTermSingleUse())
                {
                    var versioningDoc = new VersioningConfiguration
                    {
                        Default = new VersioningConfigurationCollection
                        {
                            Active = true,
                            MinimumRevisionsToKeep = 5,
                        },
                        Collections = new Dictionary<string, VersioningConfigurationCollection>
                        {
                            ["Users"] = new VersioningConfigurationCollection
                            {
                                Active = true
                            },
                            ["Dons"] = new VersioningConfigurationCollection
                            {
                                Active = true,
                            }
                        }
                    };

                    await Server.ServerStore.ModifyDatabaseVersioning(context,
                        store.Database,
                        EntityToBlittable.ConvertEntityToBlittable(versioningDoc,
                            new DocumentConventions(),
                            context));
                }

                for (int i = 0; i < 10; i++)
                {
                    for (var j = 0; j < 10; j++)
                    {
                        using (var session = store.OpenSession())
                        {
                            session.Store(new User
                            {
                                Name = $"users{i} ver {j}",
                                Age=j
                            }, "users/" + i);

                            session.Store(new Company()
                            {
                                Name = $"dons{i} ver {j}"
                            }, "companies/" + i);

                            session.SaveChanges();
                        }
                    }
                }

                using (var sub = store.Subscriptions.Open<Result>(new SubscriptionConnectionOptions(subscriptionId)))
                {
                    var mre = new AsyncManualResetEvent();
                    var names = new HashSet<string>();
                    sub.Subscribe(x =>
                    {
                        names.Add(x.Id + x.Age);
                        if (names.Count == 90)
                            mre.Set();
                    });
                    sub.Start();

                    Assert.True(await mre.WaitAsync(_reasonableWaitTime));

                }
            }
        }

        [Fact]
        public async Task VersionedSubscriptionsWithCustomScriptCompareDocs()
        {
            using (var store = GetDocumentStore())
            {

                var subscriptionId = await store.AsyncSubscriptions.CreateAsync(new SubscriptionCreationOptions<User>
                {
                    Criteria = new SubscriptionCriteria<User>
                    {
                        Script = @"
                        if(!!this.Current && !!this.Previous && this.Current.Age > this.Previous.Age)
                        {
                            return { Id: this.Current[""@metadata""][""@id""], Age: this.Current.Age }
                        }
                        else return false;
                        ",
                        IsVersioned = true

                    }
                });

                using (var context = JsonOperationContext.ShortTermSingleUse())
                {
                    var versioningDoc = new VersioningConfiguration
                    {
                        Default = new VersioningConfigurationCollection
                        {
                            Active = true,
                            MinimumRevisionsToKeep = 5,
                        },
                        Collections = new Dictionary<string, VersioningConfigurationCollection>
                        {
                            ["Users"] = new VersioningConfigurationCollection
                            {
                                Active = true
                            },
                            ["Dons"] = new VersioningConfigurationCollection
                            {
                                Active = true,
                            }
                        }
                    };

                    await Server.ServerStore.ModifyDatabaseVersioning(context,
                        store.Database,
                        EntityToBlittable.ConvertEntityToBlittable(versioningDoc,
                            new DocumentConventions(),
                            context));
                }

                for (int i = 0; i < 10; i++)
                {
                    for (var j = 0; j < 10; j++)
                    {
                        using (var session = store.OpenSession())
                        {
                            session.Store(new User
                            {
                                Name = $"users{i} ver {j}",
                                Age = j
                            }, "users/" + i);

                            session.Store(new Company()
                            {
                                Name = $"dons{i} ver {j}"
                            }, "companies/" + i);

                            session.SaveChanges();
                        }
                    }
                }

                using (var sub = store.Subscriptions.Open<Result>(new SubscriptionConnectionOptions(subscriptionId)))
                {
                    var mre = new AsyncManualResetEvent();
                    var names = new HashSet<string>();
                    var maxAge = -1;
                    sub.Subscribe(x =>
                    {
                        if (x.Age > maxAge )
                        {
                            names.Add(x.Id + x.Age);
                            maxAge = x.Age;
                        }
                        
                        if (names.Count == 9)
                            mre.Set();
                    });
                    sub.Start();

                    Assert.True(await mre.WaitAsync(_reasonableWaitTime));

                }
            }
        }
    }
}
