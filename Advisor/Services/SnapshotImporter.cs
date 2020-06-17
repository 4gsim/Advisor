using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Importing;
using Hearthstone_Deck_Tracker.Utility.Logging;
using HtmlAgilityPack;
using MahApps.Metro.Converters;
using Newtonsoft.Json;

namespace HDT.Plugins.Advisor.Services.MetaStats
{
    public class SnapshotImporter
    {
        private static readonly IDictionary<int, string> HsReplayClassIdToName = new Dictionary<int, string>
        {
            {14, "DemonHunter"},
            {10, "Warrior"},
            {8, "Shaman"},
            {7, "Rogue"},
            {5, "Paladin"},
            {3 , "Hunter"},
            {2, "Druid"},
            {9, "Warlock"},
            {4, "Mage"},
            {6, "Priest"},
        };
        private static readonly IDictionary<string, int> HsReplayNameToClassId = new Dictionary<string, int>
        {
            {"DEMONHUNTER", 14},
            {"WARRIOR", 10},
            {"SHAMAN", 8},
            {"ROGUE", 7},
            {"PALADIN", 5},
            {"HUNTER", 3},
            {"DRUID", 2},
            {"WARLOCK", 9},
            {"MAGE", 4},
            {"PRIEST", 6},
        };

        private const string ArchetypeTag = "Archetype";
        private const string PluginTag = "Advisor";

        private static int _decksFound;
        private static int _decksImported;

        private readonly TrackerRepository _tracker;

        public SnapshotImporter(TrackerRepository tracker)
        {
            _tracker = tracker;
            _decksFound = 0;
            _decksImported = 0;
        }

        /// <summary>
        ///     Task to import decks.
        /// </summary>
        /// <param name="archive">Option to auto-archive imported decks</param>
        /// <param name="deletePrevious">Option to delete all previously imported decks</param>
        /// <param name="shortenName">Option to shorten the deck title</param>
        /// <param name="progress">Tuple of two integers holding the progress information for the UI</param>
        /// <returns>The number of imported decks</returns>
        public async Task<int> ImportDecks(bool archive, bool deletePrevious, bool shortenName, IProgress<Tuple<int, int>> progress)
        {
            Log.Info("Starting archetype deck import");

            // Delete previous snapshot decks
            if (deletePrevious)
            {
                DeleteDecks();
            }

            // Get HSReplay archetypes
            var archetypes = await GetHsReplayArchetypes();

            var htmlWeb = new HtmlWeb();
            var document = htmlWeb.Load("http://metastats.net/decks/");

            // Get link for each Metastats class
            var classSites = document.DocumentNode.SelectNodes("//div[@id='meta-nav']/ul/li/a/@href");

            var tasks = classSites.Select(l => l.GetAttributeValue("href", string.Empty))
                            .Select(u => Task.Run(() => GetMetastatsClassDecks(u, progress)))
                            .ToList();
            tasks.Add(Task.Run(() => GetHsreplayDecks(archetypes, "RANKED_STANDARD", progress)));
            tasks.Add(Task.Run(() => GetHsreplayDecks(archetypes, "RANKED_WILD", progress)));

            // Wait for all threads to finish, then combine results
            var results = await Task.WhenAll(tasks);
            var decks = results.SelectMany(r => r).ToList();

            decks = filterDuplicates(decks);

            Log.Info($"Saving {decks.Count} decks to the decklist.");

            // Add all decks to the tracker
            var deckCount = await Task.Run(() => SaveDecks(decks, archive, shortenName));

            if (deckCount == decks.Count)
            {
                Log.Info($"Import of {deckCount} archetype decks completed.");
            }
            else
            {
                Log.Error($"Only {deckCount} of {decks.Count} archetype could be imported. Connection problems?");
            }

            return deckCount;
        }

        /// <summary>
        ///     Save a list of decks to the Decklist.
        /// </summary>
        /// <param name="decks">A list of HDT decks</param>
        /// <param name="archive">Flag if the decks should be auto-archived</param>
        /// <param name="shortenName">Flag if class name and website name should be removed from the deck name</param>
        /// <returns></returns>
        private int SaveDecks(IEnumerable<Deck> decks, bool archive, bool shortenName)
        {
            var deckCount = 0;

            foreach (var deck in decks)
            {
                if (deck == null)
                {
                    throw new ImportException("At least one deck couldn't be imported. Connection problems?");
                }

                Log.Info($"Importing deck ({deck.Name})");

                // Optionally remove player class from deck name
                // E.g. 'Control Warrior' => 'Control'
                var deckName = deck.Name;
                if (shortenName)
                {
                    deckName = deckName.Replace(deck.Class, "").Trim();
                    deckName = deckName.Replace("Demon Hunter", "");
                    deckName = deckName.Replace("- MetaStats ", "");
                    deckName = deckName.Replace("  ", " ");
                }

                _tracker.AddDeck(deckName, deck, archive, ArchetypeTag, PluginTag);
                deckCount++;
            }

            DeckList.Save();
            return deckCount;
        }

        private async Task<IDictionary<int, HsReplayArchetype>> GetHsReplayArchetypes()
        {
            var json = await ImportingHelper.JsonRequest($"https://hsreplay.net/api/v1/archetypes/");

            var archetypes = JsonConvert.DeserializeObject<List<HsReplayArchetype>>(json);

            return archetypes.ToDictionary(a => a.Id, a => a);
        }

        /// <summary>
        ///     Gets all decks from HSReplay.
        /// </summary>
        /// <param name="archetypes">The HSReplay archetypes</param>
        /// <param name="progress">Tuple of two integers holding the progress information for the UI</param>
        /// <returns>The list of all parsed decks</returns>
        private async Task<IList<Deck>> GetHsreplayDecks(IDictionary<int, HsReplayArchetype> archetypes, string gameType, IProgress<Tuple<int, int>> progress)
        {
            var pattern = @"(\[\d+,[12]{1}\])";

            var json = await ImportingHelper.JsonRequest($"https://hsreplay.net/analytics/query/list_decks_by_win_rate_v2/?GameType={gameType}");

            var decks = JsonConvert.DeserializeObject<HsReplayDecks>(json).Series.Data;

            return decks.SelectMany(x => decks[x.Key].Select(d =>
            {
                // Count found decks thread-safe
                Interlocked.Increment(ref _decksFound);

                // Get the archetype or default
                HsReplayArchetype archetype;
                if (!archetypes.ContainsKey(d.ArchetypeId))
                {
                    archetype = new HsReplayArchetype();
                    archetype.Name = "Other";
                    archetype.Url = $"/archetypes/{d.ArchetypeId}";
                    archetype.Class = HsReplayNameToClassId[x.Key];
                }
                else
                {
                    archetype = archetypes[d.ArchetypeId];
                }

                // Create new deck
                var deck = new Deck();

                deck.Name = archetype.Name;
                deck.Url = archetype.Url;
                deck.Class = HsReplayClassIdToName[archetype.Class];

                // Insert deck note for statistics
                deck.Note = $"#Games: {d.TotalGames}, #Win Rate: {d.WinRate}%";

                // Set import datetime as LastEdited
                deck.LastEdited = DateTime.Now;

                var matches = Regex.Matches(d.DeckList, pattern);
                foreach (Match match in matches)
                {
                    var matchText = match.Value.Trim('[', ']');

                    // Get card from database with dbf id
                    var card = Database.GetCardFromDbfId(int.Parse(matchText.Split(',')[0]));
                    card.Count = int.Parse(matchText.Split(',')[1]);

                    deck.Cards.Add(card);
                }

                // Count imported decks thread-safe
                Interlocked.Increment(ref _decksImported);

                // Report progress for UI
                progress.Report(new Tuple<int, int>(_decksImported, _decksFound));

                return deck;
            })).ToList();
        }

        /// <summary>
        ///     Gets all Metastats decks for a given class URL.
        /// </summary>
        /// <param name="classPath">The path of the class in the url</param>
        /// <param name="progress">Tuple of two integers holding the progress information for the UI</param>
        /// <returns>The list of all parsed decks</returns>
        private async Task<IList<Deck>> GetMetastatsClassDecks(string classPath, IProgress<Tuple<int, int>> progress)
        {
            var htmlWeb = new HtmlWeb();
            var document = htmlWeb.Load($"http://metastats.net/{classPath}");

            var deckSites = document.DocumentNode.SelectNodes("//div[@class='decklist']");

            if (deckSites == null)
            {
                Log.Info($"No decks found for {classPath}");
                return new List<Deck>();
            }

            // Count found decks thread-safe
            Interlocked.Add(ref _decksFound, deckSites.Count);

            // Report progress for UI
            progress.Report(new Tuple<int, int>(_decksImported, _decksFound));

            var decks = new List<Deck>();

            foreach (var site in deckSites)
            {
                // Extract link
                var link = site.SelectSingleNode("./h4/a/@href");
                var hrefValue = link.GetAttributeValue("href", string.Empty);

                // Parse and check deck ID
                var strId = Regex.Match(hrefValue, @"/deck/([0-9]+)/").Groups[1].Value;
                if (string.IsNullOrEmpty(strId))
                {
                    Interlocked.Decrement(ref _decksFound);
                    continue;
                }

                // Extract info
                var stats = site.SelectSingleNode("./div");
                var innerText = string.Join(", ", stats.InnerText.Trim().Split('\n').Select(s => s.Trim()));

                // Create deck from site
                var result = await Task.Run(() => GetMetastatsDeck(hrefValue, progress));

                // Add info to the deck
                result.Note = innerText;

                // Add Guid to the deck
                result.DeckId = new Guid(strId.PadLeft(32, '0'));

                // Set import datetime as LastEdited
                result.LastEdited = DateTime.Now;

                // Add deck to the decks list
                decks.Add(result);
            }

            return decks;
        }

        /// <summary>
        ///     Gets a Metastats deck from the meta description of a website.
        /// </summary>
        /// <param name="deckPath">The path of the deck in the url</param>
        /// <param name="progress">Tuple of two integers holding the progress information for the UI</param>
        /// <returns>The parsed deck</returns>
        private async Task<Deck> GetMetastatsDeck(string deckPath, IProgress<Tuple<int, int>> progress)
        {
            // Create deck from metatags
            var result = await MetaTagImporter.TryFindDeck($"http://metastats.net/{deckPath}");

            // Count imported decks thread-safe
            Interlocked.Increment(ref _decksImported);

            // Report progress for UI
            progress.Report(new Tuple<int, int>(_decksImported, _decksFound));

            return result;
        }

        private List<Deck> filterDuplicates(List<Deck> allDecks)
        {
            // TODO maybe return other deck than the first found
            return allDecks.GroupBy(d => d.Cards.OrderBy(c => c.DbfIf)
                    .Aggregate("", (s, c) => $"{s};{c.DbfIf}/{c.Count}")).Select(g => g.First()).ToList();
        }

        /// <summary>
        ///     Deletes all decks with Plugin tag.
        /// </summary>
        public int DeleteDecks()
        {
            Log.Info("Deleting all archetype decks");
            return _tracker.DeleteAllDecksWithTag(PluginTag);
        }

        public class HsReplayArchetype
        {
            [JsonProperty("id")]
            public int Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("player_class")]
            public int Class { get; set; }

            [JsonProperty("url")]
            public string Url { get; set; }
        }

        public class HsReplayDeck
        {
            [JsonProperty("archetype_id")]
            public int ArchetypeId { get; set; }

            [JsonProperty("deck_list")]
            public string DeckList { get; set; }

            [JsonProperty("total_games")]
            public int TotalGames { get; set; }

            [JsonProperty("win_rate")]
            public float WinRate { get; set; }
        }

        public class HsReplayDecksSeries
        {
            [JsonProperty("data")]
            public Dictionary<string, List<HsReplayDeck>> Data;
        }

        public class HsReplayDecks
        {
            [JsonProperty("series")]
            public HsReplayDecksSeries Series { get; set; }
        }
    }
}