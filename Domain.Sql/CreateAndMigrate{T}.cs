// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using Microsoft.Its.Domain.Sql.Migrations;
using Microsoft.Its.Recipes;

namespace Microsoft.Its.Domain.Sql
{
    /// <summary>
    /// Creates a database if it doesn't exist, and runs any migrations that have not been applied.
    /// </summary>
    /// <typeparam name="TContext">The type of the database context.</typeparam>
    /// <seealso cref="System.Data.Entity.IDatabaseInitializer{TContext}" />
    public class CreateAndMigrate<TContext> :
        IDatabaseInitializer<TContext>
        where TContext : DbContext
    {
        private readonly IDbMigrator[] migrators;
        private static bool bypassInitialization;

        public CreateAndMigrate() : this(new IDbMigrator[0])
        {
        }

        public CreateAndMigrate(IDbMigrator[] migrators)
        {
            this.migrators = Migrator.CreateMigratorsFromEmbeddedResourcesFor<TContext>()
                                     .Concat(migrators)
                                     .OrEmpty()
                                     .ToArray();
        }

        public void InitializeDatabase(TContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            if (bypassInitialization)
            {
                return;
            }

            if (!context.Database.Exists())
            {
                var created = CreateDatabaseIfNotExists(context);

                if (!created)
                {
                    // another concurrent caller created the database, so return and let them run the migrations
                    return;
                }
            }

            if (CurrentUserHasWritePermissions(context))
            {
                context.EnsureDatabaseIsUpToDate(migrators);
            }
        }

        private bool CreateDatabaseIfNotExists(TContext context)
        {
            try
            {
                if (context.IsAzureDatabase())
                {
                    // create the database
                    context.CreateAzureDatabase();

                    // this triggers the initializer, which then throws because the schema hasn't been initialized, so we have to suspend initialization momentarily
                    bypassInitialization = true;

                    // create the initial schema
                    var sql = ((IObjectContextAdapter)context)
                        .ObjectContext
                        .CreateDatabaseScript();

                    context.OpenConnection()
                           .Execute(sql);

                    return true;
                }

                return context.Database.CreateIfNotExists();
            }
            catch (SqlException exception)
            {
                if (exception.Number == 1801) // database already exists
                {
                    return false;
                }

                throw;
            }
            catch (AutomaticMigrationsDisabledException)
            {
                return false;
            }
            finally
            {
                bypassInitialization = false;
            }
        }

        private bool CurrentUserHasWritePermissions(TContext context)
        {
            const string HasPermsSql = "SELECT TOP(1) " +
                                        "HAS_PERMS_BY_NAME(" +
                                         "QUOTENAME(DB_NAME()) + '.' + " +
                                         "QUOTENAME(OBJECT_SCHEMA_NAME(object_id)) + '.' + " +
                                         "QUOTENAME(name), " +
                                         "N'OBJECT', " +
                                         "N'INSERT') " +
                                       "FROM sys.tables;";
            var result = context.Database.SqlQuery<int?>(HasPermsSql).Single();
            return (result ?? 0) == 1;
        }
    }
}