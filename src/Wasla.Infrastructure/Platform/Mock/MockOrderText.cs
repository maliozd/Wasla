using Wasla.Application.Abstractions.Printing;

namespace Wasla.Infrastructure.Platform.Mock;

internal static class MockOrderText
{
    private static readonly Dictionary<string, Dictionary<string, string>> Table = Build();

    public static string Resolve(string? culture, string key)
    {
        var language = ReceiptLanguageCodes.Normalize(culture);
        if (language == ReceiptLanguageCodes.Russian)
            language = ReceiptLanguageCodes.English;

        if (Table.TryGetValue(key, out var row))
        {
            if (row.TryGetValue(language, out var text))
                return text;
            if (row.TryGetValue(ReceiptLanguageCodes.Turkish, out text))
                return text;
        }

        return key;
    }

    public static bool Contains(string key) => Table.ContainsKey(key);

    private static Dictionary<string, Dictionary<string, string>> Build()
    {
        var table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        void Add(string key, string tr, string en, string ar) =>
            table[key] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ReceiptLanguageCodes.Turkish] = tr,
                [ReceiptLanguageCodes.English] = en,
                [ReceiptLanguageCodes.Arabic] = ar
            };

        Add("Mock.Modifier.NoOnion", "Soğansız", "No onion", "بدون بصل");
        Add("Mock.Modifier.NoPickles", "Turşusuz", "No pickles", "بدون مخلل");
        Add("Mock.Modifier.ExtraCheddar", "Ekstra çedar", "Extra cheddar", "شيدر إضافي");
        Add("Mock.Modifier.ExtraSauce", "Ekstra sos", "Extra sauce", "صلصة إضافية");
        Add("Mock.Modifier.LargeFries", "Büyük patates", "Large fries", "بطاطس كبيرة");
        Add("Mock.Modifier.RegularFries", "Normal patates", "Regular fries", "بطاطس عادية");
        Add("Mock.Modifier.Cola", "Kola", "Cola", "كولا");
        Add("Mock.Modifier.ColaZero", "Kola zero", "Cola zero", "كولا زيرو");
        Add("Mock.Modifier.Fanta", "Fanta", "Fanta", "فانتا");
        Add("Mock.Modifier.Ayran", "Ayran", "Ayran", "عيران");
        Add("Mock.Modifier.Small", "Küçük", "Small", "صغير");
        Add("Mock.Modifier.Medium", "Orta", "Medium", "وسط");
        Add("Mock.Modifier.Large", "Büyük", "Large", "كبير");
        Add("Mock.Modifier.ThinCrust", "İnce hamur", "Thin crust", "عجينة رقيقة");
        Add("Mock.Modifier.ClassicCrust", "Klasik hamur", "Classic crust", "عجينة كلاسيكية");
        Add("Mock.Modifier.ExtraCheese", "Ekstra peynir", "Extra cheese", "جبن إضافي");
        Add("Mock.Modifier.NoMushrooms", "Mantar olmasın", "No mushrooms", "بدون فطر");
        Add("Mock.Modifier.NoOlives", "Zeytin olmasın", "No olives", "بدون زيتون");
        Add("Mock.Modifier.ParsleyOnTheSide", "Maydanoz ayrı", "Parsley on the side", "بقدونس على الجانب");
        Add("Mock.Modifier.ExtraLemon", "Ekstra limon", "Extra lemon", "ليمون إضافي");
        Add("Mock.Modifier.Spicy", "Acılı", "Spicy", "حار");
        Add("Mock.Modifier.NotSpicy", "Acısız", "Not spicy", "غير حار");
        Add("Mock.Modifier.NoTomato", "Domates olmasın", "No tomato", "بدون طماطم");
        Add("Mock.Modifier.Fries", "Patates kızartması", "Fries", "بطاطس مقلية");
        Add("Mock.Modifier.Bulgur", "Bulgur", "Bulgur", "برغل");
        Add("Mock.Modifier.Rice", "Pilav", "Rice", "أرز");
        Add("Mock.Modifier.ExtraLavash", "Ekstra lavaş", "Extra lavash", "خبز إضافي");
        Add("Mock.Modifier.Hot", "Sıcak", "Hot", "ساخن");
        Add("Mock.Modifier.Iced", "Buzlu", "Iced", "مثلج");
        Add("Mock.Modifier.ExtraShot", "Ekstra shot", "Extra shot", "شوت إضافي");
        Add("Mock.Modifier.LactoseFreeMilk", "Laktozsuz süt", "Lactose-free milk", "حليب خالٍ من اللاكتوز");
        Add("Mock.Modifier.OatMilk", "Yulaf sütü", "Oat milk", "حليب الشوفان");
        Add("Mock.Modifier.NoSugar", "Şekersiz", "No sugar", "بدون سكر");
        Add("Mock.Modifier.WithCheese", "Peynirli", "With cheese", "مع جبن");
        Add("Mock.Modifier.WithoutCheese", "Peynirsiz", "Without cheese", "بدون جبن");
        Add("Mock.Modifier.ExtraBread", "Ekstra ekmek", "Extra bread", "خبز إضافي");
        Add("Mock.Modifier.ExtraTea", "Ekstra çay", "Extra tea", "شاي إضافي");
        Add("Mock.Modifier.NoBread", "Ekmeksiz", "No bread", "بدون خبز");
        Add("Mock.Modifier.Yogurt", "Yoğurt", "Yogurt", "زبادي");
        Add("Mock.Modifier.SinglePortion", "Tek porsiyon", "Single portion", "حصة واحدة");
        Add("Mock.Modifier.DoublePortion", "Çift porsiyon", "Double portion", "حصتان");
        Add("Mock.Modifier.Pistachio", "Antep fıstıklı", "Pistachio", "بالفستق");
        Add("Mock.Modifier.ChocolateSauce", "Çikolata sosu", "Chocolate sauce", "صلصة شوكولاتة");
        Add("Mock.Modifier.IceCreamOnTheSide", "Yanında dondurma", "Ice cream on the side", "آيس كريم على الجانب");
        Add("Mock.Modifier.ExtraSoySauce", "Ekstra soya sosu", "Extra soy sauce", "صلصة صويا إضافية");
        Add("Mock.Modifier.WasabiOnTheSide", "Vasabi ayrı", "Wasabi on the side", "وسابي على الجانب");
        Add("Mock.Modifier.NoWasabi", "Vasabisiz", "No wasabi", "بدون وسابي");
        Add("Mock.Modifier.ExtraGinger", "Ekstra zencefil", "Extra ginger", "زنجبيل إضافي");
        Add("Mock.Modifier.SpicyMayo", "Acılı mayonez", "Spicy mayo", "مايونيز حار");
        Add("Mock.Stress.Modifier.LongName", "Az acılı, soğanı az, sosu ayrı ve ekstra çıtır olsun lütfen", "Mild spice, light onion, sauce on the side, and extra crispy please", "حار قليلًا، بصل قليل، الصلصة على الجانب، ومقرمش أكثر من فضلكم");

        Add("Mock.ItemNote.NotTooSpicy", "Çok acı olmasın lütfen.", "Please don't make it too spicy.", "يرجى ألا يكون حارًا جدًا.");
        Add("Mock.ItemNote.NoOnion", "Soğan koymayın lütfen.", "No onion, please.", "بدون بصل من فضلكم.");
        Add("Mock.ItemNote.SauceOnTheSide", "Sos ayrı gelsin.", "Sauce on the side, please.", "الصلصة على الجانب من فضلكم.");
        Add("Mock.ItemNote.NoWasabi", "Vasabi koymayın.", "Please leave out the wasabi.", "بدون وسابي من فضلكم.");
        Add("Mock.ItemNote.NoSugar", "Şekersiz olsun.", "No sugar, please.", "بدون سكر من فضلكم.");
        Add("Mock.Stress.ItemNote.Long", "Lütfen soğanı çıkarın, sosu ayrı kapta gönderin ve mümkünse az acılı hazırlayın. Ekmek ayrı poşette olsun.", "Please remove the onion, send the sauce in a separate cup, keep it mild, and pack the bread separately.", "يرجى إزالة البصل وإرسال الصلصة في كوب منفصل وتحضيره قليل الحرارة ووضع الخبز في كيس منفصل.");

        Add("Mock.OrderNote.DoNotRingBell", "Zile basmayın, lütfen arayın.", "Please don't ring the bell. Call instead.", "يرجى عدم قرع الجرس، اتصلوا بدلاً من ذلك.");
        Add("Mock.OrderNote.LeaveAtDoor", "Kapıya bırakabilirsiniz.", "You can leave it at the door.", "يمكنكم تركه عند الباب.");
        Add("Mock.OrderNote.NotTooSpicy", "Lütfen çok acılı olmasın.", "Please don't make it too spicy.", "يرجى ألا يكون حارًا جدًا.");
        Add("Mock.OrderNote.SauceOnTheSide", "Sosu ayrı gönderin lütfen.", "Sauce on the side, please.", "الصلصة على الجانب من فضلكم.");
        Add("Mock.Stress.OrderNote.Long", "Zile basmayın, telefonla arayın. Siparişi kapının önüne bırakabilirsiniz. Soslar ayrı olsun ve soğan az olsun lütfen. Bebek uyuyor.", "Please don't ring the bell, call instead. You can leave the order by the door. Keep the sauces separate and use little onion. The baby is asleep.", "يرجى عدم قرع الجرس والاتصال بنا. يمكن ترك الطلب أمام الباب. اجعلوا الصلصات منفصلة والبصل قليلًا. الطفل نائم.");

        Add("Mock.Burger.Product.Cheeseburger", "Cheeseburger", "Cheeseburger", "تشيز برغر");
        Add("Mock.Burger.Product.DoubleCheeseburger", "Double cheeseburger", "Double cheeseburger", "دبل تشيز برغر");
        Add("Mock.Burger.Product.ChickenBurger", "Tavuk burger", "Chicken burger", "برغر دجاج");
        Add("Mock.Burger.Product.SmashBurger", "Smash burger", "Smash burger", "سماش برغر");
        Add("Mock.Burger.Product.CrispyChickenBurger", "Çıtır tavuk burger", "Crispy chicken burger", "برغر دجاج مقرمش");
        Add("Mock.Burger.Product.BurgerMenu", "Burger menü", "Burger menu", "وجبة برغر");
        Add("Mock.Burger.Product.ChickenBurgerMenu", "Tavuk burger menü", "Chicken burger menu", "وجبة برغر دجاج");
        Add("Mock.Burger.Product.OnionRings", "Soğan halkası", "Onion rings", "حلقات بصل");
        Add("Mock.Burger.Product.FrenchFries", "Patates kızartması", "French fries", "بطاطس مقلية");

        Add("Mock.Pizza.Product.Margherita", "Margarita pizza", "Margherita pizza", "بيتزا مارغريتا");
        Add("Mock.Pizza.Product.Mixed", "Karışık pizza", "Mixed pizza", "بيتزا مشكلة");
        Add("Mock.Pizza.Product.Sucuk", "Sucuklu pizza", "Sucuk pizza", "بيتزا سجق");
        Add("Mock.Pizza.Product.Mushroom", "Mantarlı pizza", "Mushroom pizza", "بيتزا بالفطر");
        Add("Mock.Pizza.Product.FourCheese", "Dört peynirli pizza", "Four cheese pizza", "بيتزا أربعة أجبان");
        Add("Mock.Pizza.Product.Chicken", "Tavuklu pizza", "Chicken pizza", "بيتزا دجاج");

        Add("Mock.Pide.Product.Lahmacun", "Lahmacun", "Lahmacun", "لحم بعجين");
        Add("Mock.Pide.Product.CheesePide", "Kaşarlı pide", "Cheese pide", "بيدا بالجبن");
        Add("Mock.Pide.Product.MincedMeatPide", "Kıymalı pide", "Minced meat pide", "بيدا باللحم المفروم");
        Add("Mock.Pide.Product.CubedMeatPide", "Kuşbaşılı pide", "Cubed meat pide", "بيدا بلحم مكعبات");
        Add("Mock.Pide.Product.MixedPide", "Karışık pide", "Mixed pide", "بيدا مشكلة");

        Add("Mock.Doner.Product.ChickenWrap", "Tavuk döner dürüm", "Chicken döner wrap", "راب دونر دجاج");
        Add("Mock.Doner.Product.BeefWrap", "Et döner dürüm", "Beef döner wrap", "راب دونر لحم");
        Add("Mock.Doner.Product.ChickenPlate", "Tavuk döner porsiyon", "Chicken döner plate", "صحن دونر دجاج");
        Add("Mock.Doner.Product.BeefPlate", "Et döner porsiyon", "Beef döner plate", "صحن دونر لحم");
        Add("Mock.Doner.Product.Menu", "Döner menü", "Döner menu", "وجبة دونر");

        Add("Mock.Kebab.Product.Adana", "Adana kebap", "Adana kebab", "كباب أضنة");
        Add("Mock.Kebab.Product.Urfa", "Urfa kebap", "Urfa kebab", "كباب أورفة");
        Add("Mock.Kebab.Product.ChickenShish", "Tavuk şiş", "Chicken shish", "شيش دجاج");
        Add("Mock.Kebab.Product.BeefShish", "Et şiş", "Beef shish", "شيش لحم");
        Add("Mock.Kebab.Product.MixedGrill", "Karışık ızgara", "Mixed grill", "مشاوي مشكلة");
        Add("Mock.Kebab.Product.Kofte", "Köfte", "Köfte", "كفتة");

        Add("Mock.Cafe.Product.Americano", "Americano", "Americano", "أمريكانو");
        Add("Mock.Cafe.Product.Latte", "Latte", "Latte", "لاتيه");
        Add("Mock.Cafe.Product.Cappuccino", "Cappuccino", "Cappuccino", "كابتشينو");
        Add("Mock.Cafe.Product.IcedAmericano", "Buzlu americano", "Iced americano", "أمريكانو مثلج");
        Add("Mock.Cafe.Product.IcedLatte", "Buzlu latte", "Iced latte", "لاتيه مثلج");
        Add("Mock.Cafe.Product.Tea", "Çay", "Tea", "شاي");
        Add("Mock.Cafe.Product.SanSebastian", "San Sebastian cheesecake", "San Sebastian cheesecake", "تشيز كيك سان سيباستيان");
        Add("Mock.Cafe.Product.Brownie", "Brownie", "Brownie", "براوني");
        Add("Mock.Cafe.Product.Croissant", "Kruvasan", "Croissant", "كرواسون");

        Add("Mock.Breakfast.Product.Plate", "Kahvaltı tabağı", "Breakfast plate", "طبق إفطار");
        Add("Mock.Breakfast.Product.Menemen", "Menemen", "Menemen", "مينمين");
        Add("Mock.Breakfast.Product.Omelette", "Omlet", "Omelette", "أومليت");
        Add("Mock.Breakfast.Product.Toast", "Tost", "Toast", "توست");
        Add("Mock.Breakfast.Product.Simit", "Simit", "Simit", "سميط");
        Add("Mock.Breakfast.Product.Tea", "Çay", "Tea", "شاي");

        Add("Mock.Dessert.Product.Baklava", "Baklava", "Baklava", "بقلاوة");
        Add("Mock.Dessert.Product.ColdBaklava", "Soğuk baklava", "Cold baklava", "بقلاوة باردة");
        Add("Mock.Dessert.Product.Kunefe", "Künefe", "Künefe", "كنافة");
        Add("Mock.Dessert.Product.RicePudding", "Sütlaç", "Rice pudding", "أرز بالحليب");
        Add("Mock.Dessert.Product.Profiterole", "Profiterol", "Profiterole", "بروفيترول");
        Add("Mock.Dessert.Product.ChocolateCake", "Çikolatalı pasta", "Chocolate cake", "كيك شوكولاتة");

        Add("Mock.Sushi.Product.CaliforniaRoll", "California roll", "California roll", "رول كاليفورنيا");
        Add("Mock.Sushi.Product.PhiladelphiaRoll", "Philadelphia roll", "Philadelphia roll", "رول فيلادلفيا");
        Add("Mock.Sushi.Product.SalmonRoll", "Somon roll", "Salmon roll", "رول سلمون");
        Add("Mock.Sushi.Product.ShrimpTempuraRoll", "Karides tempura roll", "Shrimp tempura roll", "رول روبيان تمبورا");
        Add("Mock.Sushi.Product.NigiriSet", "Nigiri set", "Nigiri set", "طقم نيجيري");
        Add("Mock.Sushi.Product.SushiMix", "Suşi karışık", "Sushi mix", "سوشي مشكل");

        Add("Mock.Home.Product.LentilSoup", "Mercimek çorbası", "Lentil soup", "شوربة عدس");
        Add("Mock.Home.Product.Rice", "Pirinç pilavı", "Rice", "أرز");
        Add("Mock.Home.Product.BulgurPilaf", "Bulgur pilavı", "Bulgur pilaf", "برغل");
        Add("Mock.Home.Product.DryBeans", "Kuru fasulye", "Dry beans", "فاصولياء يابسة");
        Add("Mock.Home.Product.Chickpeas", "Nohut", "Chickpeas", "حمص");
        Add("Mock.Home.Product.Meatballs", "Köfte", "Meatballs", "كرات لحم");
        Add("Mock.Home.Product.ChickenSaute", "Tavuk sote", "Chicken sauté", "دجاج سوتيه");
        Add("Mock.Home.Product.Cacik", "Cacık", "Cacık", "جاجيك");

        Add("Mock.Steak.Product.Ribeye", "Antrikot", "Ribeye", "ريب آي");
        Add("Mock.Steak.Product.Tenderloin", "Bonfile", "Tenderloin", "فيليه");
        Add("Mock.Steak.Product.GrilledMeatballs", "Izgara köfte", "Grilled meatballs", "كفتة مشوية");
        Add("Mock.Steak.Product.LambChops", "Kuzu pirzola", "Lamb chops", "ريش غنم");
        Add("Mock.Steak.Product.MashedPotato", "Patates püresi", "Mashed potato", "بطاطا مهروسة");

        Add("Mock.Seafood.Product.SeaBass", "Izgara levrek", "Grilled sea bass", "قاروص مشوي");
        Add("Mock.Seafood.Product.Calamari", "Kalamar tava", "Fried calamari", "كالاماري مقلي");
        Add("Mock.Seafood.Product.Shrimp", "Karides güveç", "Shrimp casserole", "طاجن روبيان");
        Add("Mock.Seafood.Product.FishSandwich", "Balık ekmek", "Fish sandwich", "ساندويتش سمك");
        Add("Mock.Seafood.Product.Salad", "Mevsim salata", "Seasonal salad", "سلطة موسمية");

        Add("Mock.Manti.Product.Traditional", "Kayseri mantısı", "Traditional manti", "مانتي تقليدي");
        Add("Mock.Manti.Product.Fried", "Kızarmış mantı", "Fried manti", "مانتي مقلي");
        Add("Mock.Manti.Product.Mini", "Mini mantı", "Mini manti", "مانتي صغير");

        Add("Mock.Soup.Product.Lentil", "Mercimek çorbası", "Lentil soup", "شوربة عدس");
        Add("Mock.Soup.Product.Ezogelin", "Ezogelin çorbası", "Ezogelin soup", "شوربة إيزو غيلين");
        Add("Mock.Soup.Product.Chicken", "Tavuk çorbası", "Chicken soup", "شوربة دجاج");
        Add("Mock.Soup.Product.Tripe", "İşkembe çorbası", "Tripe soup", "شوربة كرشة");
        Add("Mock.Soup.Product.Tomato", "Domates çorbası", "Tomato soup", "شوربة طماطم");

        Add("Mock.Chicken.Product.Bucket", "Tavuk kovası", "Chicken bucket", "دلو دجاج");
        Add("Mock.Chicken.Product.Wings", "Çıtır kanat", "Crispy wings", "أجنحة مقرمشة");
        Add("Mock.Chicken.Product.Wrap", "Tavuk dürüm", "Chicken wrap", "راب دجاج");
        Add("Mock.Chicken.Product.Plate", "Tavuk porsiyon", "Chicken plate", "صحن دجاج");
        Add("Mock.Chicken.Product.Fries", "Patates kızartması", "Fries", "بطاطس مقلية");

        Add("Mock.CigKofte.Product.Portion", "Çiğ köfte porsiyon", "Çiğ köfte portion", "حصة تشي كفتة");
        Add("Mock.CigKofte.Product.Wrap", "Çiğ köfte dürüm", "Çiğ köfte wrap", "راب تشي كفتة");
        Add("Mock.CigKofte.Product.Half", "Yarım çiğ köfte", "Half çiğ köfte", "نصف تشي كفتة");
        Add("Mock.CigKofte.Product.Ayran", "Ayran", "Ayran", "عيران");

        Add("Mock.Toast.Product.Mixed", "Karışık tost", "Mixed toast", "توست مشكل");
        Add("Mock.Toast.Product.Cheese", "Kaşarlı tost", "Cheese toast", "توست جبن");
        Add("Mock.Toast.Product.Sucuk", "Sucuklu tost", "Sucuk toast", "توست سجق");
        Add("Mock.Toast.Product.ChickenSandwich", "Tavuklu sandviç", "Chicken sandwich", "ساندويتش دجاج");
        Add("Mock.Toast.Product.Fries", "Patates kızartması", "Fries", "بطاطس مقلية");

        Add("Mock.Patisserie.Product.Pogaca", "Peynirli poğaça", "Cheese pastry", "معجنات بالجبن");
        Add("Mock.Patisserie.Product.Acma", "Açma", "Soft pastry roll", "معجنات طرية");
        Add("Mock.Patisserie.Product.Simit", "Simit", "Simit", "سميط");
        Add("Mock.Patisserie.Product.Cake", "Dilim pasta", "Cake slice", "قطعة كيك");
        Add("Mock.Patisserie.Product.Cookie", "Kurabiye", "Cookie", "بسكويت");

        Add("Mock.Borek.Product.CheeseBorek", "Peynirli börek", "Cheese börek", "بورك بالجبن");
        Add("Mock.Borek.Product.SpinachBorek", "Ispanaklı börek", "Spinach börek", "بورك بالسبانخ");
        Add("Mock.Borek.Product.MeatBorek", "Kıymalı börek", "Minced meat börek", "بورك باللحم");
        Add("Mock.Borek.Product.PotatoGozleme", "Patatesli gözleme", "Potato gözleme", "غوزلمة بالبطاطا");
        Add("Mock.Borek.Product.CheeseGozleme", "Peynirli gözleme", "Cheese gözleme", "غوزلمة بالجبن");

        Add("Mock.IceCream.Product.Cone", "Külah dondurma", "Ice cream cone", "آيس كريم قمع");
        Add("Mock.IceCream.Product.Cup", "Kup dondurma", "Ice cream cup", "آيس كريم كوب");
        Add("Mock.IceCream.Product.Pistachio", "Fıstıklı dondurma", "Pistachio ice cream", "آيس كريم فستق");
        Add("Mock.IceCream.Product.Chocolate", "Çikolatalı dondurma", "Chocolate ice cream", "آيس كريم شوكولاتة");
        Add("Mock.IceCream.Product.Mixed", "Karışık dondurma", "Mixed ice cream", "آيس كريم مشكل");

        Add("Mock.Waffle.Product.Plain", "Sade waffle", "Plain waffle", "وافل سادة");
        Add("Mock.Waffle.Product.Chocolate", "Çikolatalı waffle", "Chocolate waffle", "وافل شوكولاتة");
        Add("Mock.Waffle.Product.Fruit", "Meyveli waffle", "Fruit waffle", "وافل فواكه");
        Add("Mock.Waffle.Product.Crepe", "Krep", "Crêpe", "كريب");
        Add("Mock.Waffle.Product.IceCreamCrepe", "Dondurmalı krep", "Ice cream crêpe", "كريب بآيس كريم");

        Add("Mock.Asian.Product.PadThai", "Pad thai", "Pad thai", "باد تاي");
        Add("Mock.Asian.Product.Noodles", "Erişte kasesi", "Noodle bowl", "وعاء نودلز");
        Add("Mock.Asian.Product.FriedRice", "Pirinç pilavı tavada", "Fried rice", "أرز مقلي");
        Add("Mock.Asian.Product.SpringRolls", "Spring roll", "Spring rolls", "سبرينغ رول");
        Add("Mock.Asian.Product.Curry", "Tavuklu körü", "Chicken curry", "كاري دجاج");

        Add("Mock.Italian.Product.Bolognese", "Bolonez spagetti", "Spaghetti bolognese", "سباغيتي بولونيز");
        Add("Mock.Italian.Product.Alfredo", "Alfredo makarna", "Fettuccine alfredo", "فيتوتشيني ألفريدو");
        Add("Mock.Italian.Product.Lasagna", "Lazanya", "Lasagna", "لازانيا");
        Add("Mock.Italian.Product.Ravioli", "Ravioli", "Ravioli", "رافيولي");
        Add("Mock.Italian.Product.GarlicBread", "Sarımsaklı ekmek", "Garlic bread", "خبز بالثوم");

        Add("Mock.World.Product.Falafel", "Falafel tabağı", "Falafel plate", "طبق فلافل");
        Add("Mock.World.Product.Tacos", "Taco", "Tacos", "تاكو");
        Add("Mock.World.Product.Curry", "Köri", "Curry", "كاري");
        Add("Mock.World.Product.Burrito", "Burrito", "Burrito", "بوريتو");
        Add("Mock.World.Product.Hummus", "Humus", "Hummus", "حمص");

        Add("Mock.Vegan.Product.Bowl", "Vegan kase", "Vegan bowl", "وعاء نباتي");
        Add("Mock.Vegan.Product.LentilBalls", "Mercimek köftesi", "Lentil patties", "كفتة عدس");
        Add("Mock.Vegan.Product.ChickpeaStew", "Nohut yemeği", "Chickpea stew", "يخنة حمص");
        Add("Mock.Vegan.Product.Salad", "Mevsim salata", "Seasonal salad", "سلطة موسمية");
        Add("Mock.Vegan.Product.Juice", "Taze sıkılmış meyve suyu", "Fresh juice", "عصير طازج");

        Add("Mock.Healthy.Product.ChickenSalad", "Izgara tavuk salata", "Grilled chicken salad", "سلطة دجاج مشوي");
        Add("Mock.Healthy.Product.FitBowl", "Fit kase", "Fit bowl", "وعاء فتنس");
        Add("Mock.Healthy.Product.OatBowl", "Yulaf kasesi", "Oat bowl", "وعاء شوفان");
        Add("Mock.Healthy.Product.ProteinPlate", "Protein tabağı", "Protein plate", "طبق بروتين");
        Add("Mock.Healthy.Product.Ayran", "Ayran", "Ayran", "عيران");

        Add("Mock.Other.Product.DailyPlate", "Günün tabağı", "Daily plate", "طبق اليوم");
        Add("Mock.Other.Product.HouseSpecial", "Ev spesiyali", "House special", "طبق البيت");
        Add("Mock.Other.Product.SideSalad", "Yan salata", "Side salad", "سلطة جانبية");
        Add("Mock.Other.Product.Drink", "İçecek", "Drink", "مشروب");
        Add("Mock.Other.Product.Bread", "Ekmek", "Bread", "خبز");

        Add("Mock.Generic.Product.HousePlate", "Ev tabağı", "House plate", "طبق البيت");
        Add("Mock.Generic.Product.DailySoup", "Günün çorbası", "Soup of the day", "شوربة اليوم");
        Add("Mock.Generic.Product.Bread", "Ekmek", "Bread", "خبز");
        Add("Mock.Generic.Product.Ayran", "Ayran", "Ayran", "عيران");
        Add("Mock.Generic.Product.MixedPlate", "Karışık tabak", "Mixed plate", "طبق مشكل");

        Add("Mock.Stress.Product.LongName", "Fırında sebzeli, az soğanlı ve sosu ayrı servis edilen ev usulü karışık tabak", "House mixed plate with roasted vegetables, light onion, and sauce served on the side", "طبق بيتي مشكل بالخضار المشوية وبصل قليل وصلصة تقدم على الجانب");

        return table;
    }
}
