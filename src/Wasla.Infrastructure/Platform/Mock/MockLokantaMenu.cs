namespace Wasla.Infrastructure.Platform.Mock;

/// <summary>
/// Static menu for the fictional Mengen Lokantası demo.
/// Regional dishes are a curated subset of publicly documented Bolu/Mengen foods
/// (Mengen Kaymakamlığı, Kültür Portalı, Bolu İl Kültür ve Turizm). Everyday
/// lokanta dishes stay the majority. Nothing here is loaded from the network.
/// </summary>
internal static class MockLokantaMenu
{
    internal static IReadOnlyList<MockProduct> Products => Catalog;

    internal static MockProduct? FindByName(string name)
    {
        foreach (var product in Catalog)
        {
            if (product.Name == name)
                return product;
        }

        return null;
    }

    internal static IReadOnlyList<MockModifier> ModifiersFor(MockModifierProfile profile) => profile switch
    {
        MockModifierProfile.Soup => SoupModifiers,
        MockModifierProfile.OffalSoup => OffalModifiers,
        MockModifierProfile.Legume => LegumeModifiers,
        MockModifierProfile.CookedMain => CookedModifiers,
        MockModifierProfile.Grill => GrillModifiers,
        MockModifierProfile.GrillWithRice => GrillWithRiceModifiers,
        MockModifierProfile.Vegetable => VegetableModifiers,
        MockModifierProfile.Rice => RiceModifiers,
        MockModifierProfile.Erişte => EristeModifiers,
        MockModifierProfile.Salad => SaladModifiers,
        MockModifierProfile.Gavurdagi => GavurdagiModifiers,
        MockModifierProfile.Cacik => CacikModifiers,
        MockModifierProfile.MilkDessert => MilkDessertModifiers,
        MockModifierProfile.NutDessert => NutDessertModifiers,
        MockModifierProfile.SyrupDessert => SyrupDessertModifiers,
        _ => Array.Empty<MockModifier>()
    };

    internal static IReadOnlyList<string> NotesFor(MockNoteProfile profile) => profile switch
    {
        MockNoteProfile.Soup => SoupNotes,
        MockNoteProfile.Offal => OffalNotes,
        MockNoteProfile.Legume => LegumeNotes,
        MockNoteProfile.Grill => GrillNotes,
        MockNoteProfile.Chicken => ChickenNotes,
        MockNoteProfile.Meat => MeatNotes,
        MockNoteProfile.Vegetable => VegetableNotes,
        MockNoteProfile.Rice => RiceNotes,
        MockNoteProfile.Salad => SaladNotes,
        MockNoteProfile.Cacik => CacikNotes,
        MockNoteProfile.Yogurt => YogurtNotes,
        MockNoteProfile.Pickle => PickleNotes,
        MockNoteProfile.MilkDessert => MilkDessertNotes,
        MockNoteProfile.NutDessert => NutDessertNotes,
        MockNoteProfile.SyrupDessert => SyrupDessertNotes,
        MockNoteProfile.Drink => DrinkNotes,
        MockNoteProfile.RegionalMain => RegionalMainNotes,
        MockNoteProfile.Borek => BorekNotes,
        MockNoteProfile.Erişte => EristeNotes,
        MockNoteProfile.Hosaf => HosafNotes,
        MockNoteProfile.SidePlain => SidePlainNotes,
        MockNoteProfile.Fries => FriesNotes,
        _ => Array.Empty<string>()
    };

    private static readonly MockModifier[] SoupModifiers =
    [
        new("Ekstra limon", 10m),
        new("Pul biber", 15m),
        new("Ekmek", 25m)
    ];

    private static readonly MockModifier[] OffalModifiers =
    [
        new("Ekstra sarımsak", 20m),
        new("Ekstra sirke", 10m),
        new("Pul biber", 15m),
        new("Ekmek", 25m)
    ];

    private static readonly MockModifier[] LegumeModifiers =
    [
        new("Ekstra pilav", 45m),
        new("Ekmek", 25m)
    ];

    private static readonly MockModifier[] CookedModifiers =
    [
        new("Ekstra pilav", 45m),
        new("Ekmek", 25m)
    ];

    private static readonly MockModifier[] GrillModifiers =
    [
        new("Ekstra pilav", 45m),
        new("Ekstra patates", 55m),
        new("Közlenmiş biber", 30m)
    ];

    private static readonly MockModifier[] GrillWithRiceModifiers =
    [
        new("Ekstra patates", 55m),
        new("Közlenmiş biber", 30m)
    ];

    private static readonly MockModifier[] VegetableModifiers =
    [
        new("Sarımsaklı yoğurt", 35m),
        new("Ekmek", 25m)
    ];

    private static readonly MockModifier[] RiceModifiers =
    [
        new("Ekstra pilav", 40m)
    ];

    private static readonly MockModifier[] EristeModifiers =
    [
        new("Ekstra ceviz", 25m),
        new("Ekstra tereyağı", 20m)
    ];

    private static readonly MockModifier[] SaladModifiers =
    [
        new("Limon", 10m)
    ];

    private static readonly MockModifier[] GavurdagiModifiers =
    [
        new("Limon", 10m),
        new("Nar ekşisi", 15m)
    ];

    private static readonly MockModifier[] CacikModifiers =
    [
        new("Ekstra sarımsak", 15m)
    ];

    private static readonly MockModifier[] MilkDessertModifiers =
    [
        new("Kaymak", 40m)
    ];

    private static readonly MockModifier[] NutDessertModifiers =
    [
        new("Ekstra ceviz", 25m)
    ];

    private static readonly MockModifier[] SyrupDessertModifiers =
    [
        new("Kaymak", 40m)
    ];

    private static readonly string[] SoupNotes =
    [
        "Çorba çok sıcak olsun.",
        "Limonu ayrı gönderin.",
        "Pul biberi ayrı koyun.",
        "Ekmek yumuşak olsun."
    ];

    private static readonly string[] OffalNotes =
    [
        "Sarımsak koymayın.",
        "Sirkeyi ayrı gönderin.",
        "Acısız olsun.",
        "Bol sarımsaklı olsun.",
        "Ekstra acı gönderir misiniz?"
    ];

    private static readonly string[] LegumeNotes =
    [
        "Pilavı az olsun.",
        "Yağı mümkünse az olsun.",
        "Tuz eklemeyin.",
        "Ekmek taze olsun."
    ];

    private static readonly string[] GrillNotes =
    [
        "Soğansız olsun.",
        "Et biraz iyi pişsin.",
        "Acısız olsun.",
        "Köz biber ayrı olsun.",
        "Pilavı az olsun.",
        "Patates yerine pilav olabilir mi?",
        "Ekstra acı gönderir misiniz?"
    ];

    private static readonly string[] ChickenNotes =
    [
        "İyi pişsin.",
        "Derisi olmasın.",
        "Az yağlı olsun.",
        "Pilavı az olsun."
    ];

    private static readonly string[] MeatNotes =
    [
        "Et biraz iyi pişsin.",
        "Yağı mümkünse az olsun.",
        "Tuz eklemeyin.",
        "Soğansız olsun.",
        "Pilavı az olsun."
    ];

    private static readonly string[] VegetableNotes =
    [
        "Soğanı az olsun.",
        "Sarımsak koymayın.",
        "Yoğurt ayrı gelsin.",
        "Yağı mümkünse az olsun.",
        "Tuz eklemeyin."
    ];

    private static readonly string[] RiceNotes =
    [
        "Pilavı az olsun.",
        "Tuz eklemeyin.",
        "Tereyağlı olsun."
    ];

    private static readonly string[] SaladNotes =
    [
        "Salataya soğan koymayın.",
        "Sosu ayrı olsun.",
        "Limonu ayrı gönderin."
    ];

    private static readonly string[] CacikNotes =
    [
        "Sarımsak koymayın.",
        "Az tuzlu olsun."
    ];

    private static readonly string[] YogurtNotes =
    [
        "Az tuzlu olsun.",
        "Sarımsak koymayın."
    ];

    private static readonly string[] PickleNotes =
    [
        "Acısız olsun.",
        "Az gönderin."
    ];

    private static readonly string[] MilkDessertNotes =
    [
        "Az şekerli olsun.",
        "Soğuk servis olsun.",
        "Kaymak ayrı gelsin."
    ];

    private static readonly string[] NutDessertNotes =
    [
        "Az şekerli olsun.",
        "Ceviz ayrı olsun.",
        "Porsiyon küçük olsun."
    ];

    private static readonly string[] SyrupDessertNotes =
    [
        "Az şerbetli olsun.",
        "Az şekerli olsun.",
        "Soğuk servis olsun."
    ];

    private static readonly string[] DrinkNotes =
    [
        "Bol buzlu olsun.",
        "Buz koymayın.",
        "Soğuk olsun."
    ];

    private static readonly string[] RegionalMainNotes =
    [
        "Et biraz iyi pişsin.",
        "Yağı mümkünse az olsun.",
        "Tuz eklemeyin.",
        "Sıcak olsun."
    ];

    private static readonly string[] BorekNotes =
    [
        "Az yağlı olsun.",
        "Ilık olsun."
    ];

    private static readonly string[] EristeNotes =
    [
        "Ceviz ayrı olsun.",
        "Keş az olsun.",
        "Tereyağlı olsun.",
        "Tuz eklemeyin."
    ];

    private static readonly string[] HosafNotes =
    [
        "Az şekerli olsun.",
        "Soğuk servis olsun."
    ];

    private static readonly string[] SidePlainNotes =
    [
        "Tuz eklemeyin.",
        "Az yağlı olsun.",
        "Porsiyon küçük olsun."
    ];

    private static readonly string[] FriesNotes =
    [
        "Az tuzlu olsun.",
        "Kıtır olsun.",
        "Az yağlı olsun."
    ];

    private static readonly MockProduct[] Catalog =
    [
        // Soups. Offal soups stay out of ordinary lunch pools.
        P("Mercimek Çorbası", MockCourse.Soup, 120m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.Soup, MockNoteProfile.Soup, 3),
        P("Ezogelin Çorbası", MockCourse.Soup, 125m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.Soup, MockNoteProfile.Soup),
        P("Yayla Çorbası", MockCourse.Soup, 120m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.Soup, MockNoteProfile.Soup),
        P("Tarhana Çorbası", MockCourse.Soup, 115m, MockFoodTag.Vegetarian | MockFoodTag.Budget, MockModifierProfile.Soup, MockNoteProfile.Soup),
        P("Kızılcık Tarhanası", MockCourse.Soup, 145m, MockFoodTag.Vegetarian | MockFoodTag.Regional, MockModifierProfile.Soup, MockNoteProfile.Soup, 1),
        P("Yoğurtlu Bakla Çorbası", MockCourse.Soup, 150m, MockFoodTag.Vegetarian | MockFoodTag.Regional, MockModifierProfile.Soup, MockNoteProfile.Soup, 1),
        P("İşkembe Çorbası", MockCourse.Soup, 240m, MockFoodTag.Offal, MockModifierProfile.OffalSoup, MockNoteProfile.Offal, 1),
        P("Kelle Paça", MockCourse.Soup, 280m, MockFoodTag.Offal, MockModifierProfile.OffalSoup, MockNoteProfile.Offal, 1),
        P("Tavuk Suyu Çorbası", MockCourse.Soup, 135m, MockFoodTag.Chicken | MockFoodTag.Mild, MockModifierProfile.Soup, MockNoteProfile.Soup),

        // Legumes and pot dishes.
        P("Kuru Fasulye", MockCourse.Main, 195m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Pot | MockFoodTag.Mild, MockModifierProfile.Legume, MockNoteProfile.Legume, 3),
        P("Etli Kuru Fasulye", MockCourse.Main, 265m, MockFoodTag.Meat | MockFoodTag.Pot, MockModifierProfile.Legume, MockNoteProfile.Legume),
        P("Nohut", MockCourse.Main, 190m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Pot | MockFoodTag.Mild, MockModifierProfile.Legume, MockNoteProfile.Legume, 3),
        P("Etli Nohut", MockCourse.Main, 255m, MockFoodTag.Meat | MockFoodTag.Pot, MockModifierProfile.Legume, MockNoteProfile.Legume),
        P("Barbunya", MockCourse.Main, 210m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Pot, MockModifierProfile.Legume, MockNoteProfile.Legume),
        P("Etli Barbunya", MockCourse.Main, 270m, MockFoodTag.Meat | MockFoodTag.Pot, MockModifierProfile.Legume, MockNoteProfile.Legume, 1),
        P("Yeşil Mercimek", MockCourse.Main, 185m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Pot | MockFoodTag.Mild, MockModifierProfile.Legume, MockNoteProfile.Legume),
        P("Etli Yeşil Mercimek", MockCourse.Main, 245m, MockFoodTag.Meat | MockFoodTag.Pot, MockModifierProfile.Legume, MockNoteProfile.Legume, 1),

        // Cooked meat and chicken.
        P("Tas Kebabı", MockCourse.Main, 390m, MockFoodTag.Meat | MockFoodTag.Premium | MockFoodTag.Pot | MockFoodTag.Beef, MockModifierProfile.CookedMain, MockNoteProfile.Meat, 1),
        P("Orman Kebabı", MockCourse.Main, 420m, MockFoodTag.Meat | MockFoodTag.Premium | MockFoodTag.Regional, MockModifierProfile.CookedMain, MockNoteProfile.RegionalMain, 1),
        P("İzmir Köfte", MockCourse.Main, 360m, MockFoodTag.Meat | MockFoodTag.Beef | MockFoodTag.Mild, MockModifierProfile.CookedMain, MockNoteProfile.Meat),
        P("Hasanpaşa Köfte", MockCourse.Main, 375m, MockFoodTag.Meat | MockFoodTag.Beef | MockFoodTag.Mild, MockModifierProfile.CookedMain, MockNoteProfile.Meat, 1),
        P("Patlıcan Musakka", MockCourse.Main, 320m, MockFoodTag.Meat | MockFoodTag.Mild, MockModifierProfile.CookedMain, MockNoteProfile.Meat),
        P("Et Sote", MockCourse.Main, 385m, MockFoodTag.Meat | MockFoodTag.Premium | MockFoodTag.Beef, MockModifierProfile.CookedMain, MockNoteProfile.Meat, 1),
        P("Tavuk Sote", MockCourse.Main, 275m, MockFoodTag.Chicken | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.CookedMain, MockNoteProfile.Chicken, 3),
        P("Dana Kavurma", MockCourse.Main, 450m, MockFoodTag.Meat | MockFoodTag.Premium | MockFoodTag.Beef, MockModifierProfile.CookedMain, MockNoteProfile.Meat, 1),
        P("Hünkar Beğendi", MockCourse.Main, 490m, MockFoodTag.Meat | MockFoodTag.Premium, MockModifierProfile.CookedMain, MockNoteProfile.Meat, 1),
        P("Kuzu İncik", MockCourse.Main, 640m, MockFoodTag.Meat | MockFoodTag.Premium, MockModifierProfile.CookedMain, MockNoteProfile.Meat, 1),
        P("Etli Türlü", MockCourse.Main, 340m, MockFoodTag.Meat | MockFoodTag.Pot, MockModifierProfile.CookedMain, MockNoteProfile.Meat),
        P("Etli Bezelye", MockCourse.Main, 335m, MockFoodTag.Meat, MockModifierProfile.CookedMain, MockNoteProfile.Meat),
        P("Fırın Tavuk", MockCourse.Main, 295m, MockFoodTag.Chicken | MockFoodTag.Mild, MockModifierProfile.CookedMain, MockNoteProfile.Chicken),
        P("Tavuk Haşlama", MockCourse.Main, 265m, MockFoodTag.Chicken | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.CookedMain, MockNoteProfile.Chicken, 3),

        // Documented Mengen / Bolu plates. Not every order uses these.
        P("Mengen Kuzu Güveç", MockCourse.Main, 650m, MockFoodTag.Meat | MockFoodTag.Premium | MockFoodTag.Regional, MockModifierProfile.CookedMain, MockNoteProfile.RegionalMain, 1),
        P("Mengen Pilavı", MockCourse.Main, 360m, MockFoodTag.Meat | MockFoodTag.Regional, MockModifierProfile.Rice, MockNoteProfile.RegionalMain),
        P("Paşa Pilavı", MockCourse.Main, 380m, MockFoodTag.Meat | MockFoodTag.Regional, MockModifierProfile.Rice, MockNoteProfile.RegionalMain, 1),
        P("Kaldırık Dolması", MockCourse.Main, 245m, MockFoodTag.Vegetarian | MockFoodTag.Regional, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Bakla Çullaması", MockCourse.Main, 230m, MockFoodTag.Vegetarian | MockFoodTag.Regional, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable, 1),
        P("Kaşık Sapı", MockCourse.Main, 210m, MockFoodTag.Vegetarian | MockFoodTag.Regional | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),

        // Grills.
        P("Izgara Köfte", MockCourse.Main, 340m, MockFoodTag.Meat | MockFoodTag.Grill | MockFoodTag.Beef | MockFoodTag.Mild, MockModifierProfile.Grill, MockNoteProfile.Grill, 3),
        P("Kasap Köfte", MockCourse.Main, 390m, MockFoodTag.Meat | MockFoodTag.Grill | MockFoodTag.Beef | MockFoodTag.Mild, MockModifierProfile.Grill, MockNoteProfile.Grill),
        P("Tavuk Şiş", MockCourse.Main, 310m, MockFoodTag.Chicken | MockFoodTag.Grill | MockFoodTag.Mild, MockModifierProfile.Grill, MockNoteProfile.Chicken),
        P("Tavuk Izgara", MockCourse.Main, 295m, MockFoodTag.Chicken | MockFoodTag.Grill | MockFoodTag.Mild, MockModifierProfile.Grill, MockNoteProfile.Chicken),
        P("Tavuk Kanat", MockCourse.Main, 280m, MockFoodTag.Chicken | MockFoodTag.Grill | MockFoodTag.Mild, MockModifierProfile.Grill, MockNoteProfile.Chicken),
        P("Karışık Izgara", MockCourse.Main, 620m, MockFoodTag.Meat | MockFoodTag.Grill | MockFoodTag.Premium, MockModifierProfile.Grill, MockNoteProfile.Grill, 1),
        P("Köfte ve Pilav", MockCourse.Main, 375m, MockFoodTag.Meat | MockFoodTag.Grill | MockFoodTag.Beef | MockFoodTag.Mild, MockModifierProfile.GrillWithRice, MockNoteProfile.Grill),
        P("Dana Izgara", MockCourse.Main, 490m, MockFoodTag.Meat | MockFoodTag.Grill | MockFoodTag.Premium | MockFoodTag.Beef, MockModifierProfile.Grill, MockNoteProfile.Grill, 1),
        P("Kuzu Şiş", MockCourse.Main, 560m, MockFoodTag.Meat | MockFoodTag.Grill | MockFoodTag.Premium, MockModifierProfile.Grill, MockNoteProfile.Grill, 1),

        // Vegetable and olive-oil dishes. Karnıyarık and etli bamya contain meat.
        P("Karnıyarık", MockCourse.Main, 285m, MockFoodTag.Meat | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("İmam Bayıldı", MockCourse.Main, 240m, MockFoodTag.Vegetarian, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Taze Fasulye", MockCourse.Main, 210m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Türlü", MockCourse.Main, 220m, MockFoodTag.Vegetarian | MockFoodTag.Pot | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Kabak Yemeği", MockCourse.Main, 195m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Ispanak", MockCourse.Main, 200m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Pırasa", MockCourse.Main, 205m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Zeytinyağlı Bamya", MockCourse.Main, 215m, MockFoodTag.Vegetarian, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),
        P("Etli Bamya", MockCourse.Main, 275m, MockFoodTag.Meat | MockFoodTag.Pot, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable, 1),
        P("Zeytinyağlı Yaprak Sarma", MockCourse.Main, 235m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.Vegetable, MockNoteProfile.Vegetable),

        // Rice, pasta, and sides.
        P("Pirinç Pilavı", MockCourse.Side, 110m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.Rice, MockNoteProfile.Rice, 4),
        P("Bulgur Pilavı", MockCourse.Side, 100m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.Rice, MockNoteProfile.Rice, 3),
        P("Şehriyeli Pirinç Pilavı", MockCourse.Side, 120m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.Rice, MockNoteProfile.Rice),
        P("Keşli Cevizli Erişte", MockCourse.Side, 210m, MockFoodTag.Vegetarian | MockFoodTag.Regional, MockModifierProfile.Erişte, MockNoteProfile.Erişte, 1, lightMain: true),
        P("Makarna", MockCourse.Side, 120m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.SidePlain),
        P("Patates Püresi", MockCourse.Side, 110m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.SidePlain),
        P("Patates Kızartması", MockCourse.Side, 130m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.Fries),
        P("Sade Erişte", MockCourse.Side, 150m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.SidePlain, 1),
        P("Mengen Su Böreği", MockCourse.Side, 180m, MockFoodTag.Meat | MockFoodTag.Regional, MockModifierProfile.None, MockNoteProfile.Borek, 1),

        // Cold sides and salads.
        P("Cacık", MockCourse.Salad, 95m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.Cacik, MockNoteProfile.Cacik, 3),
        P("Yoğurt", MockCourse.Salad, 80m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.Yogurt, 3),
        P("Çoban Salata", MockCourse.Salad, 130m, MockFoodTag.Vegetarian, MockModifierProfile.Salad, MockNoteProfile.Salad),
        P("Mevsim Salata", MockCourse.Salad, 125m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.Salad, MockNoteProfile.Salad),
        P("Gavurdağı Salata", MockCourse.Salad, 155m, MockFoodTag.Vegetarian, MockModifierProfile.Gavurdagi, MockNoteProfile.Salad, 1),
        P("Turşu", MockCourse.Salad, 70m, MockFoodTag.Vegetarian | MockFoodTag.Budget, MockModifierProfile.None, MockNoteProfile.Pickle),
        P("Piyaz", MockCourse.Salad, 115m, MockFoodTag.Vegetarian | MockFoodTag.Budget, MockModifierProfile.Salad, MockNoteProfile.Salad),
        P("Roka Salata", MockCourse.Salad, 140m, MockFoodTag.Vegetarian, MockModifierProfile.Salad, MockNoteProfile.Salad, 1),

        // Desserts. Kedi batmaz and höşmerim are the Bolu regional sweets.
        P("Fırın Sütlaç", MockCourse.Dessert, 130m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.MilkDessert, MockNoteProfile.MilkDessert, 3),
        P("Höşmerim", MockCourse.Dessert, 175m, MockFoodTag.Vegetarian | MockFoodTag.Regional, MockModifierProfile.NutDessert, MockNoteProfile.NutDessert, 1),
        P("Tel Kadayıf", MockCourse.Dessert, 160m, MockFoodTag.Vegetarian, MockModifierProfile.NutDessert, MockNoteProfile.NutDessert, 1),
        P("Kemalpaşa", MockCourse.Dessert, 145m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.SyrupDessert, MockNoteProfile.SyrupDessert),
        P("Revani", MockCourse.Dessert, 140m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.SyrupDessert, MockNoteProfile.SyrupDessert),
        P("Şekerpare", MockCourse.Dessert, 140m, MockFoodTag.Vegetarian, MockModifierProfile.SyrupDessert, MockNoteProfile.SyrupDessert),
        P("İrmik Helvası", MockCourse.Dessert, 135m, MockFoodTag.Vegetarian | MockFoodTag.Regional | MockFoodTag.Mild, MockModifierProfile.NutDessert, MockNoteProfile.NutDessert),
        P("Kedi Batmaz", MockCourse.Dessert, 170m, MockFoodTag.Vegetarian | MockFoodTag.Regional, MockModifierProfile.NutDessert, MockNoteProfile.NutDessert, 1),
        P("Kabak Hoşafı", MockCourse.Dessert, 95m, MockFoodTag.Vegetarian | MockFoodTag.Regional | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.Hosaf),
        P("Kazandibi", MockCourse.Dessert, 155m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.MilkDessert, MockNoteProfile.MilkDessert),

        // Drinks.
        P("Ayran", MockCourse.Drink, 60m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.Drink, 3),
        P("Su", MockCourse.Drink, 40m, MockFoodTag.Vegetarian | MockFoodTag.Budget | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.Drink, 3),
        P("Soda", MockCourse.Drink, 45m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.Drink, 1),
        P("Şalgam", MockCourse.Drink, 65m, MockFoodTag.Vegetarian, MockModifierProfile.None, MockNoteProfile.Drink),
        P("Kola", MockCourse.Drink, 80m, MockFoodTag.Vegetarian, MockModifierProfile.None, MockNoteProfile.Drink),
        P("Kola Zero", MockCourse.Drink, 80m, MockFoodTag.Vegetarian, MockModifierProfile.None, MockNoteProfile.Drink, 1),
        P("Gazoz", MockCourse.Drink, 70m, MockFoodTag.Vegetarian | MockFoodTag.Mild, MockModifierProfile.None, MockNoteProfile.Drink)
    ];

    private static MockProduct P(
        string name,
        MockCourse course,
        decimal price,
        MockFoodTag tags,
        MockModifierProfile modifiers,
        MockNoteProfile notes,
        int weight = 2,
        bool lightMain = false) =>
        new(name, course, price, tags, modifiers, notes, weight, lightMain);
}

internal enum MockCourse
{
    Soup,
    Main,
    Side,
    Salad,
    Dessert,
    Drink
}

[Flags]
internal enum MockFoodTag
{
    None = 0,
    Vegetarian = 1,
    Chicken = 2,
    Meat = 4,
    Offal = 8,
    Regional = 16,
    Budget = 32,
    Premium = 64,
    Mild = 128,
    Grill = 256,
    Pot = 512,
    Beef = 1024
}

internal enum MockModifierProfile
{
    None,
    Soup,
    OffalSoup,
    Legume,
    CookedMain,
    Grill,
    GrillWithRice,
    Vegetable,
    Rice,
    Erişte,
    Salad,
    Gavurdagi,
    Cacik,
    MilkDessert,
    NutDessert,
    SyrupDessert
}

internal enum MockNoteProfile
{
    None,
    Soup,
    Offal,
    Legume,
    Grill,
    Chicken,
    Meat,
    Vegetable,
    Rice,
    Salad,
    Cacik,
    Yogurt,
    Pickle,
    MilkDessert,
    NutDessert,
    SyrupDessert,
    Drink,
    RegionalMain,
    Borek,
    Erişte,
    Hosaf,
    SidePlain,
    Fries
}

internal sealed record MockProduct(
    string Name,
    MockCourse Course,
    decimal Price,
    MockFoodTag Tags,
    MockModifierProfile Modifiers,
    MockNoteProfile Notes,
    int Weight,
    bool LightMain);

internal readonly record struct MockModifier(string Name, decimal Price);

internal static class MockFoodTags
{
    public static bool Has(this MockFoodTag value, MockFoodTag flag) => (value & flag) == flag;
}
