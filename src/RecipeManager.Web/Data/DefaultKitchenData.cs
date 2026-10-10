namespace RecipeManager.Web.Data;

// Reference data of the seeded public kitchen; a new kitchen gets its own copy.
// The order matters: seeded IDs follow it, and the first migration created exactly these rows.
public static class DefaultKitchenData
{
    public static readonly string[] Categories = ["Breakfast", "Main Course", "Dessert", "Salad"];

    public static readonly string[] Ingredients =
        ["Egg", "Flour", "Milk", "Tomato", "Chicken Breast", "Parmesan", "Spaghetti"];
}
