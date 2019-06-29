using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaximEmmBots.Models.Json.Restaurants;
using MaximEmmBots.Models.Mongo;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace MaximEmmBots.Services.DistributionBot
{
    internal sealed class DistributionBotSheetsService
    {
        private readonly ITelegramBotClient _client;
        private readonly ILogger<GoogleSheetsService> _logger;
        private readonly GoogleSheetsService _googleSheetsService;
        private readonly CultureService _cultureService;
        private readonly Context _context;

        public DistributionBotSheetsService(ITelegramBotClient client,
            ILogger<GoogleSheetsService> logger,
            GoogleSheetsService googleSheetsService,
            CultureService cultureService,
            Context context)
        {
            _client = client;
            _logger = logger;
            _googleSheetsService = googleSheetsService;
            _cultureService = cultureService;
            _context = context;
        }

        internal Task ExecuteManyAsync(ICollection<Restaurant> restaurants, CancellationToken stoppingToken,
            int userId, DateTime? requestedDate = null)
        {
            if (restaurants.Count == 0)
                return Task.CompletedTask;

            var tasks = new Task[restaurants.Count];
            var position = 0;
            
            foreach (var restaurant in restaurants)
                tasks[position++] = ExecuteAsync(restaurant, stoppingToken, requestedDate, userId);

            return Task.WhenAll(tasks);
        }

        internal async Task ExecuteAsync(Restaurant restaurant, CancellationToken stoppingToken,
            DateTime? requestedDate = null, int userId = 0)
        {
            var forDate = requestedDate ?? _cultureService.NowFor(restaurant).AddDays(1d);
            var culture = _cultureService.CultureFor(restaurant);
            var monthName = culture.DateTimeFormat.GetMonthName(forDate.Month);
            var model = _cultureService.ModelFor(restaurant);
            
            _logger.LogDebug("Tomorrow is {0}, restaurant id is {1}", forDate, restaurant.ChatId);
            
            var range = $"{monthName} {forDate:MM/yyyy}!$A$1:$YY";
            var response =
                await _googleSheetsService.GetValueRangeAsync(restaurant.DistributionBot.SpreadsheetId, range,
                    stoppingToken);
            
            if (response?.Values == null || response.Values.Count == 0)
            {
                if (userId > 0)
                    await _client.SendTextMessageAsync(userId, model.TimeBoardIsNotAvailableForThisMonth,
                        cancellationToken: stoppingToken);
                return;
            }

            var day = forDate.Day;
            var dateText = forDate.ToString("D", culture);
            var privates = new Dictionary<int, string>();
            var users = new List<(int userId, string name, string time)>();
            
            _logger.LogTrace("Date text is {0}, restaurant id is {1}", dateText, restaurant.ChatId);
            
            foreach (var row in response.Values)
            {
                _logger.LogTrace("Columns count is {0}, restaurant id is {1}", row.Count, restaurant.ChatId);

                if (row.Count < day + 3 ||
                    !int.TryParse(row[1].ToString(), out var rowUserId) && row[2].ToString().IndexOf('+') != 0 ||
                    privates.ContainsKey(rowUserId))
                {
                    _logger.LogTrace("Neither user id nor phone exists for current row or privates contains user id");
                    continue;
                }

                var dayText = row[day + 2].ToString();
                if (string.IsNullOrWhiteSpace(dayText))
                {
                    _logger.LogDebug("Day text is null, empty or consists of white spaces for restaurant id {0}",
                        restaurant.ChatId);
                    continue;
                }

                {
                    _logger.LogTrace("Day text before letter check is {0}, restaurant id is {1}",
                        dayText, restaurant.ChatId);
                    
                    var place = dayText[0];
                    _logger.LogTrace("Place is {0} for restaurant id {1}", place, restaurant.ChatId);
                    if (char.IsLetter(place))
                    {
                        _logger.LogTrace("Place is letter for restaurant id {0}", restaurant.ChatId);
                        if (restaurant.PlaceId != place || dayText.Length == 1)
                        {
                            _logger.LogDebug("Place id doesn't equal to place or day text's length is 1 , skipping");
                            continue;
                        }

                        dayText = dayText.Substring(1).TrimStart();
                    }
                    
                    _logger.LogTrace("Day text after letter check is {0}, restaurant id is {1}",
                        dayText, restaurant.ChatId);
                }

                var name = row[0].ToString();
                
                _logger.LogTrace("Name from row[0] is {0}, user id is {1}, restaurant id is {2}",
                    name, rowUserId, restaurant.ChatId);

                var groupItem = (rowUserId, name, dayText);
                users.Add(groupItem);

                if (rowUserId == 0 || userId > 0 && rowUserId != userId)
                {
                    _logger.LogTrace("User id equals to 0 or initial id is greater than 0 and not equals to user id");
                    continue;
                }

                var youWorkAt = string.Format(model.YouWorkAt, name, dateText,
                    $"{dayText} {restaurant.PlaceInfo}".TrimEnd());
                _logger.LogDebug("YouWorkAt message is \"{0}\" for user id {1}, restaurant id is {2}",
                    youWorkAt, rowUserId, restaurant.ChatId);
                privates[rowUserId] = youWorkAt;
            }
            
            if (users.Count == 0)
                return;
            
            foreach (var (privateUserId, privateText) in privates)
            {
                var privateTextBuilder = new StringBuilder();
                privateTextBuilder.Append(privateText);
                privateTextBuilder.Append("\n\n");
                privateTextBuilder.AppendFormat(model.WhoWorksWithYou, dateText);
                privateTextBuilder.Append('\n');
                privateTextBuilder.AppendJoin('\n', users.Where(u => u.userId != privateUserId).Select(
                    u => u.userId > 0
                        ? string.Format(model.TimeForUserWithTelegram, u.name, u.time, u.userId)
                        : string.Format(model.TimeForUserWithoutTelegram, u.name, u.time)));

                try
                {
                    await _client.SendTextMessageAsync(privateUserId, privateTextBuilder.ToString(),
                        ParseMode.Html, cancellationToken: stoppingToken);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Unable to send a message to private chat");
                }

                try
                {

                    await _context.UserRestaurantPairs.UpdateOneAsync(ur => ur.RestaurantId == restaurant.ChatId &&
                                                                            ur.UserId == privateUserId,
                        Builders<UserRestaurantPair>.Update.SetOnInsert(ur => ur.Id, ObjectId.GenerateNewId()),
                        new UpdateOptions {IsUpsert = true}, stoppingToken);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Unable to save user id to UserRestaurantPairs");
                }
            }

            if (userId == 0)
            {
                var groupTextBuilder = new StringBuilder();
                groupTextBuilder.AppendFormat(model.WhoWorksAtDate, dateText, restaurant.PlaceInfo);
                groupTextBuilder.Append('\n');
                groupTextBuilder.AppendJoin('\n', users.Select(u => u.userId > 0
                    ? string.Format(model.TimeForUserWithTelegram, u.name, u.time, u.userId)
                    : string.Format(model.TimeForUserWithoutTelegram, u.name, u.time)));

                try
                {
                    await _client.SendTextMessageAsync(restaurant.ChatId, groupTextBuilder.ToString(),
                        ParseMode.Html, cancellationToken: stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to send a message to group");
                }
            }
        }
    }
}