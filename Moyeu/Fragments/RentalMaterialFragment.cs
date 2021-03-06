using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Xml.Serialization;
using System.Xml.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.Graphics;

using Android.Util;
using Android.Graphics.Drawables;
using SwipeRefreshLayout = Android.Support.V4.Widget.SwipeRefreshLayout;
using Android.Support.V4.View;
using Android.Support.V7.Widget;

namespace Moyeu
{
	public class RentalMaterialFragment : RentalFragment, IMoyeuSection
	{
		HubwayRentals rentals;
		RentalsRecyclerAdapter adapter;
		PreferencesRentalStorage storage;

		int currentPageId;
		bool hasNextPage;
		bool loading;

		SwipeRefreshLayout refreshLayout;
		RecyclerView recycler;
		LinearLayoutManager layoutManager;

		View loadingLayout;

		View loginLayout;
		Button loginBtn;
		TextView statusText;
		EditText username;
		EditText password;

		enum LayoutPhase {
			List,
			Login,
			Loading
		}

		LayoutPhase currentLayoutPhase;
		Task<Rental[]> preloadedRentals;

		public RentalMaterialFragment ()
		{
			HasOptionsMenu = false;
			AnimationExtensions.SetupFragmentTransitions (this);
		}

		protected override void InitializeList ()
		{

		}

		public override View OnCreateView (LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
		{
			var ctx = inflater.Context;
			var view = inflater.Inflate (Resource.Layout.RentalsLayout, container, false);
			refreshLayout = view.FindViewById<SwipeRefreshLayout> (Resource.Id.refresh_layout);

			refreshLayout.SetColorScheme (Resource.Color.swipe_refresh_color_main,
			                              Resource.Color.swipe_refresh_color_shade1,
			                              Resource.Color.swipe_refresh_color_shade2,
			                              Resource.Color.swipe_refresh_color_shade3);
			refreshLayout.Refresh += (sender, e) => Refresh (reset: false);

			recycler = view.FindViewById<RecyclerView> (Resource.Id.recycler);
			layoutManager = new LinearLayoutManager (ctx) {
				SmoothScrollbarEnabled = true
			};
			recycler.HasFixedSize = true;
			recycler.SetLayoutManager (layoutManager);

			loadingLayout = view.FindViewById (Resource.Id.loading_layout);

			loginLayout = view.FindViewById (Resource.Id.login_layout);
			loginBtn = loginLayout.FindViewById<Button> (Resource.Id.loginBtn);
			statusText = loginLayout.FindViewById<TextView> (Resource.Id.loginStatusText);
			username = loginLayout.FindViewById<EditText> (Resource.Id.username);
			password = loginLayout.FindViewById<EditText> (Resource.Id.password);

			SwitchLayoutPhase (LayoutPhase.List);

			return view;
		}

		public override void OnViewCreated (View view, Bundle savedInstanceState)
		{
			view.SetBackgroundDrawable (AndroidExtensions.DefaultBackground);
			var existingRentals = preloadedRentals.Result;
			adapter = new RentalsRecyclerAdapter ();
			recycler.SetAdapter (adapter);
			adapter.AppendRentals (existingRentals);
		}

		public override void OnAttach (Android.App.Activity activity)
		{
			base.OnAttach (activity);
			storage = new PreferencesRentalStorage (Activity.GetPreferences (FileCreationMode.Private));
			preloadedRentals = Task.Run ((Func<Rental[]>)storage.GetStoredRentals);
		}

		async void DoFetch (bool forceRefresh = false)
		{
			bool hadError = false;
			do {
				statusText.Visibility = hadError ? ViewStates.Visible : ViewStates.Invisible;
				var credentials = StoredCredentials;
				if (string.IsNullOrEmpty (credentials.Username) || string.IsNullOrEmpty (credentials.Password))
					SwitchLayoutPhase (LayoutPhase.Login);
				if (currentLayoutPhase == LayoutPhase.Login) {
					password.Text = string.Empty;
					StoredCredentials = credentials = await AcceptUserInputAsync ();
				}
				rentals = new HubwayRentals (StoredCookies, credentials);
				SwitchLayoutPhase (LayoutPhase.Loading);
				hadError = !(await GetRentals (forceRefresh));
				if (hadError)
					StoredCredentials = new RentalCrendentials ();
			} while (hadError);
		}

		Task<RentalCrendentials> AcceptUserInputAsync ()
		{
			var newCredentials = new TaskCompletionSource<RentalCrendentials> ();
			EventHandler handler = null;
			handler = (s, e) => {
				loginBtn.Enabled = false;
				password.Enabled = false;
				username.Enabled = false;

				loginBtn.Click -= handler;

				newCredentials.SetResult (new RentalCrendentials {
					Username = username.Text,
					Password = password.Text
				});
			};
			loginBtn.Click += handler;

			loginBtn.Enabled = true;
			password.Enabled = true;
			username.Enabled = true;

			return newCredentials.Task;
		}

		void SwitchLayoutPhase (LayoutPhase phase)
		{
			AnimationExtensions.SetupAutoSceneTransition ((ViewGroup)View);
			loadingLayout.Visibility = ViewStates.Invisible;
			refreshLayout.Visibility = ViewStates.Invisible;
			loginLayout.Visibility = ViewStates.Invisible;

			if (phase != LayoutPhase.Loading && refreshLayout != null && refreshLayout.Refreshing)
				refreshLayout.Refreshing = false;

			switch (phase) {
			case LayoutPhase.List:
				refreshLayout.Visibility = ViewStates.Visible;
				break;
			case LayoutPhase.Loading:
				// Either the adapter has no elements and we show our dedicated loadscreen
				if (refreshLayout == null || adapter == null
				    || (adapter.ItemCount == 0 && !refreshLayout.Refreshing))
					loadingLayout.Visibility = ViewStates.Visible;
				// Or we use refresh layout spinner to notify we are refreshing
				else {
					refreshLayout.Visibility = ViewStates.Visible;
					// We need a delay here to avoid animation collision
					new Handler ().PostDelayed (() => refreshLayout.Refreshing = true, 500);
				}
				break;
			case LayoutPhase.Login:
				loginLayout.Visibility = ViewStates.Visible;
				break;
			}

			currentLayoutPhase = phase;
		}

		protected override void Refresh (bool reset = true)
		{
			currentPageId = 0;
			DoFetch (forceRefresh: true);
		}

		async Task<bool> GetRentals (bool forceRefresh = false)
		{
			loading = true;
			var isFirst = currentPageId == 0;
			var newRentals = await rentals.GetRentals (currentPageId++);
			hasNextPage = newRentals != null && newRentals.Last ().Id != 1;
			if (newRentals == null) {
				currentPageId--;
				loading = false;
				return false;
			}
			SwitchLayoutPhase (LayoutPhase.List);
			var oldCount = adapter.ItemCount;
			var firstVisible = oldCount > 0 && layoutManager.FindFirstVisibleItemPosition () == 0;
			adapter.AppendRentals (newRentals);
			if (isFirst) {
				storage.StoreRentals (newRentals);
				if (firstVisible)
					recycler.ScrollToPosition (0);
			}
			loading = false;
			return true;
		}

		class RentalsRecyclerAdapter : RecyclerView.Adapter
		{
			List<Rental?> rentals = new List<Rental?> ();
			HashSet<long> rentalIds = new HashSet<long> ();

			Color basePriceColor = Color.Rgb (0x99, 0xcc, 0x00);
			Color startPriceColor = Color.Rgb (0xb8, 0x9d, 0x2b);
			Color endPriceColor = Color.Rgb (0xff, 0x44, 0x44);
			const int MaxPriceForEnd = 30;

			public RentalsRecyclerAdapter ()
			{
				this.HasStableIds = true;
			}

			public override void OnBindViewHolder (RecyclerView.ViewHolder vh, int position)
			{
				var r = rentals [position];

				if (r == null) {
					((RentalHeaderHolder)vh).Header.Text =
						rentals [position + 1].Value.DepartureTime.Date.ToLongDateString ();
					return;
				}

				var holder = (RentalItemHolder)vh;
				var rental = r.Value;
				holder.StationFrom.Text = StationUtils.CutStationName (rental.FromStationName);
				holder.StationTo.Text = StationUtils.CutStationName (rental.ToStationName);
				if (rental.Duration > TimeSpan.FromHours (1))
					holder.Time.Text = rental.Duration.ToString ("h\\:mm\\:ss");
				else
					holder.Time.Text = rental.Duration.ToString ("m\\:ss");
				var color = basePriceColor;
				if (rental.Price > 0)
					color = InterpolateColor (Math.Min (MaxPriceForEnd, rental.Price) / MaxPriceForEnd,
					                          startPriceColor,
					                          endPriceColor);
				holder.Price.SetTextColor (color);
				holder.Price.Text = rental.Price.ToString ("F2");
				holder.Chronometer.Time = rental.Duration;
			}

			public override RecyclerView.ViewHolder OnCreateViewHolder (ViewGroup parent, int viewType)
			{
				if (viewType == 0)
					return new RentalHeaderHolder (MakeHeaderView (parent.Context));

				var inflater = LayoutInflater.From (parent.Context);
				return new RentalItemHolder (inflater.Inflate (Resource.Layout.RentalItem, parent, false));
			}

			public override int ItemCount {
				get {
					return rentals == null ? 0 : rentals.Count;
				}
			}

			public override int GetItemViewType (int position)
			{
				return rentals [position] == null ? 0 : 1;
			}

			public override long GetItemId (int position)
			{
				var r = rentals [position];
				if (r == null)
					return rentals [position + 1].Value.Id << 32;
				else
					return r.Value.Id;
			}

			public void AppendRentals (IEnumerable<Rental> newRentals)
			{
				var filteredRentals = newRentals
					.Where (r => rentalIds.Add (r.Id))
					.ToList ();

				if (!filteredRentals.Any ())
					return;

				// Check if we need to insert the new data at the beginning or at the end
				// of the current collection
				var insertIndex = rentals.Count;
				if (rentals.Any ()
				    && filteredRentals.Last ().DepartureTime > rentals.First (r => r != null).Value.DepartureTime)
					insertIndex = 0;

				rentals.InsertRange (insertIndex, filteredRentals.Select (fr => (Rental?)fr));

				// Go over what we just added and insert header slots
				var addedCount = filteredRentals.Count;
				for (int i = insertIndex; i < insertIndex + addedCount; i++) {
					var r = rentals [i].Value;
					if (i == 0 || (rentals [i - 1].Value.DepartureTime.Date != r.DepartureTime.Date)) {
						rentals.Insert (i, null);
						i++;
						addedCount++;
					}
				}
				if (addedCount > 0)
					NotifyItemRangeInserted (insertIndex, addedCount);
			}

			TextView MakeHeaderView (Context context)
			{
				if (AndroidExtensions.IsMaterial)
					return (TextView)LayoutInflater.From (context).Inflate (Resource.Layout.RentalHeader, null);

				var result = new TextView (context) {
					Gravity = GravityFlags.Center,
					TextSize = 10,
				};
				result.SetPadding (4, 4, 4, 4);
				result.SetBackgroundColor (Color.Rgb (0x22, 0x22, 0x22));
				result.SetAllCaps (true);
				result.SetTypeface (Typeface.DefaultBold, TypefaceStyle.Bold);
				result.SetTextColor (Color.White);

				return result;
			}

			Color InterpolateColor (double ratio, Color startColor, Color endColor)
			{
				return Color.Rgb ((int)((endColor.R - startColor.R) * ratio + startColor.R),
				                  (int)((endColor.G - startColor.G) * ratio + startColor.G),
				                  (int)((endColor.B - startColor.B) * ratio + startColor.B));
			}
		}

		class RentalItemHolder : RecyclerView.ViewHolder
		{
			public TextView StationFrom {
				get;
				private set;
			}

			public TextView StationTo {
				get;
				private set;
			}

			public TextView Price {
				get;
				private set;
			}

			public ChronometerView Chronometer {
				get;
				private set;
			}

			public TextView Time {
				get;
				private set;
			}

			public RentalItemHolder (View view) : base (view)
			{
				StationFrom = view.FindViewById<TextView> (Resource.Id.rentalFromStation);
				StationTo = view.FindViewById<TextView> (Resource.Id.rentalToStation);
				Price = view.FindViewById<TextView> (Resource.Id.rentalPrice);
				Chronometer = view.FindViewById<ChronometerView> (Resource.Id.rentalTime);
				Time = view.FindViewById<TextView> (Resource.Id.rentalTimePrimary);
			}

			public Rental Rental {
				get;
				set;
			}
		}

		class RentalHeaderHolder : RecyclerView.ViewHolder
		{
			public TextView Header {
				get;
				private set;
			}

			public RentalHeaderHolder (View view) : base (view)
			{
				Header = (TextView)view;
			}
		}

		class PreferencesRentalStorage : IRentalStorage
		{
			const string Key = "hubwaySavedRentals";
			ISharedPreferences prefs;

			public PreferencesRentalStorage (ISharedPreferences prefs)
			{
				this.prefs = prefs;
			}

			public Rental[] GetStoredRentals ()
			{
				var savedRentals = prefs.GetString (Key, null);
				if (string.IsNullOrEmpty (savedRentals))
					return new Rental[0];
				try {
					var root = XElement.Parse (savedRentals);
					return root.Elements ("rental").Select (e => new Rental {
						Id = (long)e.Attribute ("Id"),
						FromStationName = (string)e.Attribute ("FromStationName"),
						ToStationName = (string)e.Attribute ("ToStationName"),
						Price = (double)e.Attribute ("Price"),
						DepartureTime = (DateTime)e.Attribute ("DepartureTime"),
						ArrivalTime = (DateTime)e.Attribute ("ArrivalTime"),
						Duration = (TimeSpan)e.Attribute ("Duration")
					}).ToArray ();
				} catch (Exception e) {
					AnalyticsHelper.LogException ("PreferencesRentalStorage", e);
					Log.Error ("PreferencesRentalStorage", "Couldn't deserialize existing rental: {0}", e.ToString ());
				}

				return null;
			}

			public void StoreRentals (Rental[] rentals)
			{
				var savedRentals = string.Empty;
				if (rentals != null) {
					try {
						var root = new XElement ("rentals");
						root.Add (rentals.Select (r => {
							return new XElement ("rental",
							                     new XAttribute ("Id", r.Id),
							                     new XAttribute ("FromStationName", r.FromStationName),
							                     new XAttribute ("ToStationName", r.ToStationName),
							                     new XAttribute ("Price", r.Price),
							                     new XAttribute ("DepartureTime", r.DepartureTime),
							                     new XAttribute ("ArrivalTime", r.ArrivalTime),
							                     new XAttribute ("Duration", r.Duration));
						}));
						savedRentals = root.ToString ();
					} catch (Exception e) {
						AnalyticsHelper.LogException ("PreferencesRentalStorage", e);
						Log.Error ("PreferencesRentalStorage", "Couldn't serialize existing rental: {0}", e.ToString ());
					}
				}

				using (var editor = prefs.Edit ()) {
					editor.PutString (Key, savedRentals);
					editor.Commit ();
				}
			}
		}
	}
}

