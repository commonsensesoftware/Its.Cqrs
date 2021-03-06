// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;

namespace Microsoft.Its.Domain.Sql.CommandScheduler
{
    public class CommandSchedulerDatabaseInitializer : CreateAndMigrate<CommandSchedulerDbContext>
    {
    }
}