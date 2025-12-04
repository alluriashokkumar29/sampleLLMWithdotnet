using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

//string cosmosDbEndpointUri = "https://cosmicworks2022.documents.azure.com:443/";
string cosmosDbPrimaryKey = "AccountEndpoint=https://tradingcosmosdata.documents.azure.com:443/;AccountKey=EZmm9HMG6QyukcgYwRcVzSjPaVBvlkSNPFtGAERHAeKrdlXDRyLK7vD1dG7419pimaDPlKvcgMSTACDbXte2cQ==;";
CosmosClient client = new(cosmosDbPrimaryKey);

//Database database = client.GetDatabase();

Container container = client.GetContainer("testdata", "cusers");

// Create a new product
User user = new User
{
Id = Guid.NewGuid().ToString(),
Name = "David k",
City = "Hongkong"
};

// Insert the product into the container
try
{
ItemResponse<User> response = await container.CreateItemAsync(user, new PartitionKey(user.City));
Console.WriteLine($"Product inserted with id: {response.Resource.Id}");
}
catch (CosmosException ex)
{
Console.WriteLine($"Error inserting product: {ex.Message}");
}

public class User
{
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; }
    [JsonProperty(PropertyName = "name")]
    public string Name { get; set; }
    [JsonProperty(PropertyName = "city")]
    public string City { get; set; }
}
