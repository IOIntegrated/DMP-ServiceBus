using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

class Program
{
    static readonly HttpClient client = new HttpClient();
    static readonly string functionUrl = "https://dmp-relay-func.azurewebsites.net/api/SendToDMP";
    static readonly string functionKey = "YOUR_FUNCTION_KEY"; // Replace with your actual Function Key

    static async Task Main()
    {
        Console.WriteLine("Azure Function Relay Test Client");
        Console.WriteLine("================================");
        Console.WriteLine($"Function URL: {functionUrl}");
        
        while (true)
        {
            Console.Write("\nNachricht eingeben (oder 'exit'): ");
            string input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "exit")
                break;
            
            try
            {
                Console.WriteLine("⏳ Sende Nachricht an Azure Function...");
                
                // Create the HTTP request
                var request = new HttpRequestMessage(HttpMethod.Post, functionUrl);
                
                // Add the function key as a header
                request.Headers.Add("x-functions-key", functionKey);
                
                // Set the content with JSON type
                var content = new StringContent(input, Encoding.UTF8, "application/json");
                request.Content = content;
                
                // Send the request
                var response = await client.SendAsync(request);
                
                // Process the response
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