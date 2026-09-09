using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    // Client mit 5 Minuten Timeout
    static readonly HttpClient client = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };
    static readonly string functionUrl = "https://dmp-relay-func.azurewebsites.net/api/SendToDMP";
    static readonly string functionKey = "YcvYlPoSl0fIhAv_I0UdqcwBlMy7ERRBWRajnjj27hxoAzFuuWPW3w==";
    static readonly string connectionId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    static async Task Main()
    {
        Console.WriteLine("Azure Function Relay Test Client");
        Console.WriteLine("================================");
        Console.WriteLine($"Function URL: {functionUrl}");
        Console.WriteLine($"Timeout: {client.Timeout.TotalSeconds} Sekunden");
        
        while (true)
        {
            Console.Write("\nNachricht eingeben (oder 'exit'): ");
            string input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "exit")
                break;
            
            try
            {
                Console.WriteLine("⏳ Sende Nachricht an Azure Function...");
                
                // Erstelle ein JSON-Objekt mit der Nachricht
                var messageObject = new { message = input, connectionId = connectionId };
                string jsonMessage = JsonSerializer.Serialize(messageObject);
                
                // Erstelle die HTTP-Anfrage
                var request = new HttpRequestMessage(HttpMethod.Post, functionUrl);
                
                // Function Key als Header hinzufügen
                request.Headers.Add("x-functions-key", functionKey);
                
                // Content als JSON
                var content = new StringContent(jsonMessage, Encoding.UTF8, "application/json");
                request.Content = content;
                
                // Anfrage senden
                Console.WriteLine("   Sende Anfrage... (warte auf Antwort)");
                var response = await client.SendAsync(request);
                
                // Antwort verarbeiten
                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("✅ Antwort erhalten:");
                    Console.WriteLine(responseBody);
                }
                else
                {
                    Console.WriteLine($"❌ Fehler: {response.StatusCode} - {response.ReasonPhrase}");
                    string errorDetails = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Details: {errorDetails}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Fehler beim Senden: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("   ↳ " + ex.InnerException.Message);
                }
            }
        }
        
        Console.WriteLine("❎ Test Client beendet.");
    }
}