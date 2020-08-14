set today=%DATE:~10,4%%DATE:~4,2%%DATE:~7,2%
cd C:\Users\TNguy\OneDrive\Documents\Repositories\Lean\ToolBox\bin\Debug
QuantConnect.ToolBox.exe --app=IEXDownloader --tickers=TSLA --resolution=Daily --from-date=update --to-date=%today%-00:00:00 --api-key="sk_df859f222f224b948e448abf79678a06"
PAUSE