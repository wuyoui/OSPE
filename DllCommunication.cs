﻿/*
* OSPE - Open Source Packet Editor
* Copyright(C) 2018  Javier Pereda <https://github.com/elecyb>
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation; either version 3 of the License, or
* (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using SharedMemory;
using System.Threading;
using OSPE_Filters;
using Buffer = System.Buffer;

namespace OSPE
{
    public enum ServerCodes : uint
    {
        SCODE_NOP,
        SCODE_INJECTPACKET,
        SCODE_SETPACKET,
        SCODE_STARTFILTERING,
        SCODE_STOPFILTERING,
        SCODE_STARTCAPTURE,
        SCODE_STOPCAPTURE,
        SCODE_LOADDLLEX,
        SCODE_UNLOADDLLEX,
    };

    static class DllCommunication
    {
        private static bool CheckMMFileExists(string fileName)
        {
            try
            {
                MemoryMappedFile.OpenExisting(fileName);
            }catch(FileNotFoundException)
            {
                return false;
            }
            return true;
        }

        public static void SetTargetPid(int pid)
        {
            _pktMmfName = @"OSPEPACKETBUFF" + pid;
            _cmdMmfName = @"OSPECMDBUFF" + pid;
        }


        // Memory Mapped File - Packet MMF data
        /*
                |       PacketInfo   |                      PacketInfo.Size                                |
                |        20-bytes    |------------------------------Data-----------------------------------|
                |-----------------------------------------_pktMmfBufferSize--------------------------------|
        */

        private static string _pktMmfName;
        private static int _pktMmfNodeCount = 50;
        private static int _pktMmfBufferSize = 32788;

        public static void StartPacketReaderMMF()
        {
            int nodeBufferSize = sizeof(byte) * _pktMmfBufferSize;
            SharedMemory.CircularBuffer packetMMF;
            if (!CheckMMFileExists(_pktMmfName))
                packetMMF = new SharedMemory.CircularBuffer(_pktMmfName, _pktMmfNodeCount, nodeBufferSize);
            else
            {
                packetMMF = new SharedMemory.CircularBuffer(_pktMmfName);
            }

            byte[] writtenData = new byte[_pktMmfBufferSize];

            //int threadCount = 0;
            Action reader = () =>
            {
                //int myThreadIndex = Interlocked.Increment(ref threadCount);
                //Output.outMsgBox("Started thread {0}: ", threadCount);

                for (; ; )
                {
                    //var t1 = Environment.TickCount; Debug.WriteLine("packetMMF.Read(writtenData) {0}", ID);
                    //var t2 = Environment.TickCount; if (t2-t1 > 0) Debug.WriteLine("ANTES de packetMMF.Read {0} - diff: {1}", ID, t2-t1);

                    //The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely. 
                    int amount = packetMMF.Read(writtenData, -1);

                    //var t3 = Environment.TickCount; if (t3 - t2 > 0) Debug.WriteLine("DESPUES de server.Read {0} - diff: {1}", ID, t3 - t2);

                    if (amount == 0)
                        throw new Exception("Read 0 bytes from queue!");

                    Output.outString("Read data: {0}", BitConverter.ToString(writtenData));

                    // PacketInfo HEADER
                    var packetInfo = new PacketInfo();

                    var piSize = Marshal.SizeOf(packetInfo);
                    var piData = new byte[piSize];

                    // Lee los hSize bytes correspondientes al packetInfo
                    Buffer.BlockCopy(writtenData, 0, piData, 0, piSize);

                    // Convierte los bytes al Struct (variable packetInfo)
                    IntPtr ptr = Marshal.AllocHGlobal(piSize);
                    Marshal.Copy(piData, 0, ptr, piSize);
                    packetInfo = (PacketInfo)Marshal.PtrToStructure(ptr, packetInfo.GetType());

                    // CONTENIDO  (datos de tamaño en header)
                    var cSize = packetInfo.Size;
                    var cData = new byte[cSize];

                    if (cSize > _pktMmfBufferSize)
                        Output.outError("LOST DATA!, got packet with size ({0}) bigger than {0}!", cSize, _pktMmfBufferSize);

                    Buffer.BlockCopy(writtenData, piSize, cData, 0, cSize);

                    PacketManager.PacketReceived(packetInfo, ref cData);

                    //var t4 = Environment.TickCount; if (t4- t3 > 0) Debug.WriteLine("DESPUES de PacketManager.PacketReceived {0} - diff: {1}", ID, t4 - t3);

                }
            };

            // Inicia el thread reader
            ThreadPool.QueueUserWorkItem(o => { reader(); });
             
        }


        // Memory Mapped File - Command MMF data
        /*
            |   ServerCode   |  FilterCount/newPacketLen   |             Filter/Packet Data                     |
            |    1-byte      |   1-byte   / 2-bytes        |-----------------Data-------------------------------|
            |-------------------------------------_cmdMmfBufferSize---------------------------------------------|
        */
        private static string _cmdMmfName;
        private static SharedMemory.CircularBuffer cmdMMF;

        public static void WriteCommandToCmdMMF(ServerCodes sc, byte[] data = null, UInt16 newLength = 0)
        {
            int mmfWriteTimeout = 1000;
            byte[] writtenData = new byte[FilterManager.FILTERINBYTESSIZE * FilterManager.GetActiveFilterCount() + 2]; // Minimo:  SizeOf(Filter) * cantActivos + 2 
            // Escribe en el 1er byte el comando
            writtenData[0] = (byte) sc;

            if (sc == ServerCodes.SCODE_INJECTPACKET)
            {
                Array.Copy(data, 0, writtenData, 1, newLength);                
            }
            if (sc == ServerCodes.SCODE_STARTFILTERING)
            {
                writtenData[1] = (byte)FilterManager.GetActiveFilterCount();
                byte[] bytes = FilterManager.ConvertFilterListToBytes();
                Array.Copy(bytes, 0, writtenData, 2, FilterManager.FILTERINBYTESSIZE * FilterManager.GetActiveFilterCount());
            }
            if (sc == ServerCodes.SCODE_SETPACKET)
            {
                Array.Copy(FilterManager.StructToBytes(newLength), 0, writtenData, 1, 2);
                Array.Copy(data, 0, writtenData, 3, newLength);
            }
            if (sc == ServerCodes.SCODE_LOADDLLEX)
            {
                Array.Copy(Encoding.ASCII.GetBytes(Settings.DLLEx), 0, writtenData, 1, Settings.DLLEx.Length);  
            }

            // Spawneamos un thread que puede esperar el mmfWriteTimeout para escribir el comando
            //int threadCount = 0;

            //int myThreadIndex = Interlocked.Increment(ref threadCount);

            if (CheckMMFileExists(_cmdMmfName))
                cmdMMF = new SharedMemory.CircularBuffer(_cmdMmfName);
            else
            {
                Output.outMsgBox("ERROR, MMF does not exists!"); // No esta el archivo
                return;
            }

           /* try
            {
                MemoryMappedFile.OpenExisting("Global\\"+_cmdMmfName, MemoryMappedFileRights.FullControl,System.IO.HandleInheritability.None);
            }
            catch (FileNotFoundException)
            {
                Output.outMsgBox("FAIL, NO ENCUENTA EL MMF"); // No esta el archivo
                return;
            }
            */

            //The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely. 
            int amount = cmdMMF.Write(writtenData, mmfWriteTimeout);

            if (amount == 0)
                throw new Exception("Write 0 bytes to command MMF!");

            //Output.outString("Write data: {0}", BitConverter.ToString(writtenData));

        }





    }
}