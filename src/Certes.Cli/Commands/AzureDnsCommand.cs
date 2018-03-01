﻿using System;
using System.CommandLine;
using System.Threading.Tasks;
using Certes.Cli.Settings;
using Microsoft.Azure.Management.Dns.Fluent;
using Microsoft.Azure.Management.Dns.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Newtonsoft.Json;
using NLog;

namespace Certes.Cli.Commands
{
    internal class AzureDnsCommand : AzureCommand, ICliCommand
    {
        private const string CommandText = "dns";
        private const string OrderIdParam = "order-id";
        private const string DomainParam = "domain";

        private static readonly ILogger logger = LogManager.GetLogger(nameof(AccountUpdateCommand));

        private readonly IDnsClientFactory clientFactory;

        public CommandGroup Group { get; } = CommandGroup.Azure;

        public AzureDnsCommand(
            IUserSettings userSettings,
            IAcmeContextFactory contextFactory,
            IFileUtil fileUtil,
            IDnsClientFactory clientFactory)
            : base(userSettings, contextFactory, fileUtil)
        {
            this.clientFactory = clientFactory;
        }

        public ArgumentCommand<string> Define(ArgumentSyntax syntax)
        {
            var cmd = syntax.DefineCommand(CommandText, help: Strings.HelpCommandAzureDns);

            DefineAzureOptions(syntax)
                .DefineUriParameter(OrderIdParam, help: Strings.HelpOrderId)
                .DefineParameter(DomainParam, help: Strings.HelpDomain);

            return cmd;
        }

        public async Task<object> Execute(ArgumentSyntax syntax)
        {
            var (serverUri, key) = await ReadAccountKey(syntax, true, false);
            var orderUri = syntax.GetParameter<Uri>(OrderIdParam, true);
            var domain = syntax.GetParameter<string>(DomainParam, true);
            var azureCredentials = await ReadAzureCredentials(syntax);
            var resourceGroup = syntax.GetOption<string>(AzureResourceGroupOption, true);

            var acme = ContextFactory.Create(serverUri, key);
            var orderCtx = acme.Order(orderUri);
            var authzCtx = await orderCtx.Authorization(domain)
                ?? throw new Exception(string.Format(Strings.ErrorIdentifierNotAvailable, domain));
            var challengeCtx = await authzCtx.Dns()
                ?? throw new Exception(string.Format(Strings.ErrorChallengeNotAvailable, "dns"));

            var authz = await authzCtx.Resource();
            var dnsValue = acme.AccountKey.DnsTxt(challengeCtx.Token);
            using (var client = clientFactory.Create(azureCredentials))
            {
                client.SubscriptionId = azureCredentials.DefaultSubscriptionId;
                var idValue = authz.Identifier.Value;
                var zone = await FindDnsZone(client, idValue);

                var name = "_acme-challenge." + idValue.Substring(0, idValue.Length - zone.Name.Length - 1);
                logger.Debug("Adding TXT record '{0}' for '{1}' in '{2}' zone.", dnsValue, name, zone.Name);

                var recordSet = await client.RecordSets.CreateOrUpdateAsync(
                    resourceGroup,
                    zone.Name,
                    name,
                    RecordType.TXT,
                    new RecordSetInner(
                        name: name,
                        tTL: 300,
                        txtRecords: new[] { new TxtRecord(new[] { dnsValue }) }));

                return new
                {
                    data = recordSet
                };
            }
        }

        private static async Task<ZoneInner> FindDnsZone(IDnsManagementClient client, string identifier)
        {
            var zones = await client.Zones.ListAsync();
            while (zones != null)
            {
                foreach (var zone in zones)
                {
                    if (identifier.EndsWith($".{zone.Name}"))
                    {
                        logger.Debug(() => 
                            string.Format("DNS zone:\n{0}", JsonConvert.SerializeObject(zone, Formatting.Indented)));
                        return zone;
                    }
                }

                zones = string.IsNullOrWhiteSpace(zones.NextPageLink) ? 
                    null : 
                    await client.Zones.ListNextAsync(zones.NextPageLink);
            }

            throw new Exception(string.Format(Strings.ErrorDnsZoneNotFound, identifier));
        }
    }
}
