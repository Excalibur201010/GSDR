//=================================================================
// SIOListener.cs
//=================================================================
// Copyright (C) 2005  Bob Tracy
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
//
// You may contact the author via email at: k5kdn@arrl.net
//=================================================================

#define DBG_PRINT

using System;
using System.Text;
using System.Collections;
using System.Windows.Forms; // needed for MessageBox (wjt)
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace PowerSDR
{	
	public class SIOListenerII
    {
        #region variable

        bool run_thread = false;
        Thread send_thread;
        AutoResetEvent send_event = new AutoResetEvent(false);
        byte[] send_data = new byte[1024];
//        uint data_length = 0;
        private delegate void DebugCallbackFunction(string name);
        public bool debug = false;

        #endregion

        #region Constructor

        public SIOListenerII(console c)
		{
			console = c;
			console.Closing += new System.ComponentModel.CancelEventHandler(console_Closing);
			parser = new CATParser(console);

			//event handler for Serial RX Events
			SDRSerialSupportII.SDRSerialPort.serial_rx_event += new SDRSerialSupportII.SerialRXEventHandler(SerialRXEventHandler);
		
			if ( console.CATEnabled )  // if CAT is on fire it up 
			{ 
				try 
				{ 
					enableCAT();  
				}
				catch ( Exception ex ) 
				{					
					// fixme??? how cool is to to pop a msg box from an exception handler in a constructor ?? 
					//  seems ugly to me (wjt) 
					console.CATEnabled = false; 
					if ( console.SetupForm != null ) 
					{ 
						console.SetupForm.copyCATPropsToDialogVars(); // need to make sure the props on the setup page get reset 
					}
					MessageBox.Show("Could not initialize CAT control.  Exception was:\n\n " + ex.Message + 
						"\n\nCAT control has been disabled.", "Error Initializing CAT control", 
						MessageBoxButtons.OK, MessageBoxIcon.Error);
				}
			}

			SIOMonitor = new System.Timers.Timer();
			SIOMonitor.Elapsed+=new
				System.Timers.ElapsedEventHandler(SIOMonitor_Elapsed);
            SIOMonitor.Interval = 5000;     // 5s

            run_thread = true;
            send_thread = new Thread(new ThreadStart(SendThread));
            send_thread.Name = "Serial send Process Thread ";
            send_thread.Priority = ThreadPriority.Normal;
            send_thread.IsBackground = true;
            send_thread.Start();

		}

        ~SIOListenerII()
        {
            run_thread = false;
            send_event.Set();
        }

		private void SIOMonitor_Elapsed(object sender,
			System.Timers.ElapsedEventArgs e)
		{
			if(!console.MOX) SIOMonitorCount++;		// increments the counter when in receive

			if(SIOMonitorCount > 12)	// if the counter is less than 12 (60 seconds),reinitialize the serial port
			{
				Fpass = true;
				disableCAT();
				enableCAT();
				//Initialize();
                SIOMonitorCount = 0;
			}
		}

		public void enableCAT() 
		{

			lock ( this ) 
			{
				if ( cat_enabled ) return; // nothing to do already enabled 
				cat_enabled = true; 
			}
			int port_num = console.CATPort;
            if (port_num == 0)
                return;
			SIO = new SDRSerialSupportII.SDRSerialPort(port_num);
			SIO.setCommParms(console.CATBaudRate, 
							(SDRSerialSupportII.SDRSerialPort.Parity)console.CATParity, 
							(SDRSerialSupportII.SDRSerialPort.DataBits)console.CATDataBits, 
							(SDRSerialSupportII.SDRSerialPort.StopBits)console.CATStopBits,
                            (SDRSerialSupportII.SDRSerialPort.HandshakeBits)console.CATHandshake); 
		
			Initialize();
            SIOMonitorCount = 0;
            SIOMonitor.Start();
		}

		// typically called when the end user has disabled CAT control through a UI element ... this 
		// closes the serial port and neutralized the listeners we have in place
		public void disableCAT() 
		{
			lock ( this ) 
			{
				if ( !cat_enabled )  return; /* nothing to do already disabled */ 
				cat_enabled = false; 
			}

			if ( SIO != null ) 
			{
                SIO.run = false;
                SIO.rx_event.Set();
                SIOMonitor.Stop();
				SIO.Destroy(); 
				SIO = null; 
			}
			Fpass = true; // reset init flag 
			return; 									
		}

		#endregion Constructor

		#region Variables
				
		public SDRSerialSupportII.SDRSerialPort SIO; 
		console console;
		ASCIIEncoding AE = new ASCIIEncoding();
		private bool Fpass = true;
		private bool cat_enabled = false;  // is cat currently enabled by user? 
		private System.Timers.Timer SIOMonitor;
        CATParser parser;
		private int SIOMonitorCount = 0;
		private string CommBuffer;
		#endregion variables

		#region Methods

		private static void dbgWriteLine(string s) 
		{ 
#if(!DBG_PRINT) 
			Console.dbgWriteLine("SIOListener: " + s); 
#endif
		}

		// Called when the console is activated for the first time.  
		private void Initialize()
		{	
			if(Fpass)
			{
				SIO.Create();
				Fpass = false;
			}
		}		
#if UseParser
		private char[] ParseLeftover = null; 

		// segment incoming string into CAT commands ... handle leftovers from when we read a parial 
		// 
		private void ParseString(byte[] rxdata, uint count) 
		{ 
			if ( count == 0 ) return;  // nothing to do 
			int cmd_char_count = 0; 
			int left_over_char_count = ( ParseLeftover == null ? 0 : ParseLeftover.Length ); 
			char[] cmd_chars = new char[count + left_over_char_count]; 			
			if ( ParseLeftover != null )  // seed with leftovers from last read 
			{ 
				for ( int j = 0; j < left_over_char_count; j++ )  // wjt fixme ... use C# equiv of System.arraycopy 
				{
					cmd_chars[cmd_char_count] = ParseLeftover[j]; 
					++cmd_char_count; 
				}
				ParseLeftover = null; 
			}
			for ( int j = 0; j < count; j++ )   // while we have chars to play with 
			{ 
				cmd_chars[cmd_char_count] = (char)rxdata[j]; 
				++cmd_char_count; 
				if ( rxdata[j] == ';' )  // end of cmd -- parse it and execute it 
				{ 
					string cmdword = new String(cmd_chars, 0, cmd_char_count); 
					dbgWriteLine("cmdword: >" + cmdword + "<");  
					// BT 06/08
					string answer = parser.Get(cmdword);
					byte[] out_string = AE.GetBytes(answer);
					uint result = SIO.put(out_string, (uint) out_string.Length);

					cmd_char_count = 0; // reset word counter 
				}
			} 
			// when we get here have processed all of the incoming buffer, if there's anyting 
			// in cmd_chars we need to save it as we've not pulled a full command so we stuff 
			// it in leftover for the next time we come through 
			if ( cmd_char_count != 0 ) 
			{ 
				ParseLeftover = new char[cmd_char_count]; 
				for ( int j = 0; j < cmd_char_count; j++ )  // wjt fixme ... C# equiv of Sytsem.arraycopy 
				{
					ParseLeftover[j] = cmd_chars[j]; 
				}
			} 
#if DBG_PRINT
			if ( ParseLeftover != null) 
			{
				dbgWriteLine("Leftover >" + new String(ParseLeftover) + "<"); 
			}
#endif
			return; 
		}

#endif

		#endregion Methods

		#region Events

		private void console_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
            run_thread = false;
            send_event.Set();

			if ( SIO != null ) 
			{
                SIO.run = false;
                SIO.rx_event.Set();
				SIO.Destroy(); 
			}
		}

		private void SerialRXEventHandler(object source, SDRSerialSupportII.SerialRXEvent e)
		{
            try
            {
                lock (this)
                {
                    SIOMonitorCount = 0;        // reset watch dog!
                }

                Regex rex = new Regex(".*?;");
                byte[] out_string = new byte[1024];
                CommBuffer += AE.GetString(e.buffer, 0, e.buffer.Length);
                Debug.Write(CommBuffer + "\n");

                if (console.CATRigType == 1)
                {
                    byte[] buffer = new byte[e.buffer.Length + 1];
                    byte[] question = new byte[16];
                    byte[] answer = new byte[16];
                    byte[] question1 = new byte[1];
                    int j = 0;

                    for (int i = 0; i < e.buffer.Length; i++)
                    {
                        if (e.buffer[i] == 0xfd)
                        {
                            question[j] = e.buffer[i];

                            if (question[2] == console.CATRigAddress)
                            {
                                if (debug && !console.ConsoleClosing)
                                {
                                    string dbg_msg = "";

                                    for (int k = 0; k < j + 1; k++)
                                    {
                                        dbg_msg += question[k].ToString("X").PadLeft(2, '0');
                                        dbg_msg += " ";
                                    }

                                    if (debug && !console.ConsoleClosing)
                                    {
                                        console.Invoke(new DebugCallbackFunction(console.DebugCallback),
                                            "CAT command: " + dbg_msg);
                                    }
                                }

                                if (console.CATEcho)
                                {
                                    if (question1.Length != j)
                                        question1 = new byte[j + 1];

                                    for (int k = 0; k < j + 1; k++)
                                        question1[k] = question[k];

                                    send_data = question1;           // echo
                                    send_event.Set();
                                    Thread.Sleep(10);
                                }

                                answer = parser.Get(question);
                                send_data = answer;
                                send_event.Set();
                                j = 0;
                            }
                            else
                            {

                            }
                        }
                        else
                        {
                            question[j] = e.buffer[i];
                            j++;
                        }
                    }

                    CommBuffer = "";
                }
                else
                {
                    bool split = true;

                    for (Match m = rex.Match(CommBuffer); m.Success; m = m.NextMatch())
                    {
                        split = false;
                        string answer;
                        answer = parser.Get(m.Value);

                        if (debug && !console.ConsoleClosing)
                        {
                            console.Invoke(new DebugCallbackFunction(console.DebugCallback),
                                "CAT command: " + m.Value.ToString());
                        }

                        out_string = AE.GetBytes(answer);
                        send_data = out_string;
                        send_event.Set();
                    }

                    if(!split)
                        CommBuffer = "";
                }
            }
            catch (Exception ex)
            {
                CommBuffer = "";

                if (debug && !console.ConsoleClosing)
                {
                    console.Invoke(new DebugCallbackFunction(console.DebugCallback),
                        "CAT SerialRXEvent error! \n" + ex.ToString());

                    Debug.Write(ex.ToString());
                }
            }
            finally
            {
                SIO.rx_event.Set();
            }
		}

		#endregion Events

        #region crossthread call/thread

        public void CrossThreadCallback(string command, byte[] data)
        {
            try
            {
                switch (command)
                {
                    case "send":
                        if (data.Length <= 1024)
                        {
                            lock (this)
                            {
                                send_data = data;
                            }
                        }

                        send_event.Set();
                        break;
                }
            }
            catch (Exception ex)
            {
                if (debug && !console.ConsoleClosing)
                {
                    console.Invoke(new DebugCallbackFunction(console.DebugCallback),
                        "CAT CrossThreasCallback error! \n" + ex.ToString());
                    Debug.Write(ex.ToString());
                }
            }
        }

        private void SendThread()
        {
            ASCIIEncoding buffer = new ASCIIEncoding();
            string out_string = "";

            while (run_thread)
            {
                send_event.WaitOne();

                lock (this)
                {
                    if (SIO != null)
                    {
                        SIO.put(send_data, (uint)send_data.Length);
                    }
                }

                out_string = buffer.GetString(send_data);

                if (debug && !console.ConsoleClosing)
                {
                    if (console.CATRigType == 1)
                    {
                        string dbg_msg = "";

                        for (int k = 0; k < send_data.Length; k++)
                        {
                            dbg_msg += send_data[k].ToString("X").PadLeft(2, '0');
                            dbg_msg += " ";
                        }

                        if (debug && !console.ConsoleClosing)
                            console.Invoke(new DebugCallbackFunction(console.DebugCallback),
                                "CAT answer: " + dbg_msg);
                    }
                    else
                    {
                        if (debug && !console.ConsoleClosing)
                        {
                            console.Invoke(new DebugCallbackFunction(console.DebugCallback),
                                "CAT answer: " + out_string);
                        }
                    }
                }
            }
        }

        #endregion
    }
}

