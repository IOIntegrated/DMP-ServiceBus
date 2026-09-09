using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

public class SendToDMP
{
    private readonly ILogger _logger;

    public SendToDMP(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SendToDMP>();
    }

    [Function("SendToDMP")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        _logger.LogInformation("C# HTTP trigger function processing request");
        
        try
        {
            // Request Body lesen
            string payload = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogInformation($"Empfangene Payload: {payload}");

            // JSON verarbeiten, wenn Content-Type entsprechend ist
            string messageToSend = payload;
            string connectionId = null;
            if (req.Headers.TryGetValues("Content-Type", out var contentTypes) && 
                contentTypes.Any(h => h.Contains("application/json")))
            {
                try
                {
                    // Versuchen, die Nachricht aus dem JSON zu extrahieren
                    var jsonDoc = JsonDocument.Parse(payload);
                    
                    // Versuche message-Feld oder andere mögliche Felder zu finden
                    foreach (var property in new[] { "message", "text", "content", "test" })
                    {
                        if (jsonDoc.RootElement.TryGetProperty(property, out var valueElement))
                        {
                            messageToSend = valueElement.GetString();
                            _logger.LogInformation($"Extrahierte Nachricht aus JSON '{property}'-Feld: '{messageToSend}'");
                            break;
                        }
                    }

                    if (jsonDoc.RootElement.TryGetProperty("connectionId", out var connEl))
                    {
                        connectionId = connEl.GetString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"JSON-Parsing fehlgeschlagen: {ex.Message}");
                }
            }

            var dataToSend = new { message = messageToSend };
            string jsonToSend = JsonSerializer.Serialize(dataToSend);

            // Nun verwenden wir jsonToSend statt payload
            _logger.LogInformation($"Sende an Relay: '{jsonToSend}'");

            // Получение переменных из окружения
            var sbConnection = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
            var requestQueue = Environment.GetEnvironmentVariable("RequestQueueName");
            var responseQueue = Environment.GetEnvironmentVariable("ResponseQueueName");

            // Проверка connectionId
            if (string.IsNullOrEmpty(connectionId))
            {
                _logger.LogError("ConnectionId is missing.");
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("{\"error\": \"connectionId is required\"}");
                return badRequest;
            }

            // Создание клиента Service Bus
            var client = new ServiceBusClient(sbConnection);
            var sender = client.CreateSender(requestQueue);

            // Отправка запроса
            var sbMessage = new ServiceBusMessage(jsonToSend)
            {
                SessionId = connectionId,
                ReplyToSessionId = connectionId
            };

            _logger.LogInformation($"Sending message to '{requestQueue}' with SessionId '{connectionId}'");
            await sender.SendMessageAsync(sbMessage);

            // Ожидание ответа из responses
            _logger.LogInformation("Waiting for response...");
            var receiver = await client.AcceptSessionAsync(responseQueue, connectionId, new ServiceBusSessionReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
            });

            var responseMessage = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

            string reply = responseMessage != null
                ? responseMessage.Body.ToString()
                : "No response from listener";

            _logger.LogInformation($"Response: {reply}");

            // Ответ клиенту HTTP
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync($"{{\"status\":\"success\",\"message\":{JsonSerializer.Serialize(reply)}}}");
            return response;    

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler bei der Relay-Kommunikation");
            
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            error.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await error.WriteStringAsync($"{{\"status\":\"error\",\"message\":\"{ex.Message.Replace("\"", "\\\"")}\",\"details\":\"{ex.StackTrace?.Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ")}\"}}");
            return error;
        }
    }
}