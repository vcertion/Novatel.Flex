﻿using System;
using System.Collections.Generic;
using System.Text;
using Crc32;

namespace Novatel.Flex.Networking
{
    internal class Adapter
    {
        private readonly object m_classLock = new object();
        private readonly Queue<Packet> m_outgoingPackets;

        private readonly byte m_portIdentifier;

        private readonly TransferBuffer m_receiveBuffer;
        private TransferBuffer m_currentBuffer;
        private List<Packet> m_incomingPackets;

        public Adapter(byte portIdentifier)
        {
            m_incomingPackets = new List<Packet>();
            m_outgoingPackets = new Queue<Packet>();

            m_receiveBuffer = new TransferBuffer(8192); // the minimum size is 256 bytes
            m_currentBuffer = null;

            m_portIdentifier = portIdentifier;
        }

        private static Packet FormatPacket(Packet data, bool isIncoming)
        {
            if (!isIncoming)
            {
                var bytes = data.GetBytes();
                data.WriteUInt32(Crc32Algorithm.Compute(bytes));
            }

            return data;
        }

        private bool HasPacketToSend()
        {
            return m_outgoingPackets.Count != 0;
        }

        private KeyValuePair<TransferBuffer, Packet> GetPacketToSend()
        {
            if (m_outgoingPackets.Count == 0)
                throw new NovatelNetworkException("No packets are available to send.");

            var packet = m_outgoingPackets.Dequeue();

            var formattedPacket = FormatPacket(packet, packet.IsIncoming);
            formattedPacket.Lock();
            var rawBytes = packet.GetBytes();
            return new KeyValuePair<TransferBuffer, Packet>(new TransferBuffer(rawBytes, 0, rawBytes.Length, true),
                formattedPacket);
        }

        /// <summary>
        ///     Returns a list of buffers that is ready to be sent. These buffers must be sent in order.
        /// </summary>
        /// <returns>
        ///     If no buffers are available for sending, null is returned, otherwise the outgoing packets.
        /// </returns>
        public List<KeyValuePair<TransferBuffer, Packet>> GetOutgoingPackets()
        {
            List<KeyValuePair<TransferBuffer, Packet>> buffers = null;
            lock (m_classLock)
            {
                if (HasPacketToSend())
                {
                    buffers = new List<KeyValuePair<TransferBuffer, Packet>>();
                    while (HasPacketToSend())
                    {
                        buffers.Add(GetPacketToSend());
                    }
                }
            }

            return buffers;
        }

        /// <summary>
        ///     Returns a list of all packets that are ready for processing. If no packets are available,
        ///     null is returned.
        /// </summary>
        /// <returns></returns>
        public List<Packet> GetIncomingPackets()
        {
            List<Packet> packets = null;
            lock (m_classLock)
            {
                if (m_incomingPackets.Count > 0)
                {
                    packets = new List<Packet>(m_incomingPackets);
                    m_incomingPackets = new List<Packet>();
                }
            }

            return packets;
        }

        /// <summary>
        ///     Transfers raw incoming data into the adapter object.
        /// </summary>
        /// <param name="rawBuffer"></param>
        /// <param name="isIncoming"></param>
        /// <remarks>
        ///     Call GetIncomingPackets to obtain a list of ready to process packets.
        /// </remarks>
        public void Receive(TransferBuffer rawBuffer, bool isIncoming = true)
        {
            var incomingBuffersTmp = new List<TransferBuffer>();

            lock (m_classLock)
            {
                var length = rawBuffer.Size - rawBuffer.Offset;
                var index = 0;

                while (length > 0)
                {
                    var maxLength = length;
                    var calcLength = m_receiveBuffer.Buffer.Length - m_receiveBuffer.Size;

                    if (maxLength > calcLength)
                        maxLength = calcLength;

                    length -= maxLength;

                    Buffer.BlockCopy(rawBuffer.Buffer, rawBuffer.Offset + index, m_receiveBuffer.Buffer,
                        m_receiveBuffer.Size, maxLength);

                    m_receiveBuffer.Size += maxLength;
                    index += maxLength;

                    while (m_receiveBuffer.Size > 0)
                    {
                        // if we don't have a current packet object, try to allocate one.
                        if (m_currentBuffer == null)
                        {
                            // we need atleast two bytes to allocate a packet.
                            if (m_receiveBuffer.Size < 2)
                            {
                                break;
                            }

                            // check if contains strings, if it does, drop the buffer
                            var text = Encoding.ASCII.GetString(m_receiveBuffer.Buffer);

                            if (text.Contains("#") && text.Contains(",") && text.Contains("-") && text.Contains("."))
                            {
                                length = 0;
                                break;
                            }

                            // Calculate the packet size. (Novatel FlexPak6 specific)
                            var packetSize = CheckForPrefixAndGetPacketSize(m_receiveBuffer);

                            // the standard tcp one would be:
                            // int packetSize = m_receiveBuffer.Buffer[1] << 8 | m_receiveBuffer.Buffer[0];

                            m_currentBuffer = new TransferBuffer(packetSize, 0, packetSize);
                        }

                        // Calculate how many bytes are left to receive in the packet.
                        var maxCopyCount = m_currentBuffer.Size - m_currentBuffer.Offset;

                        // If we need more bytes than we currently have, update the size.
                        if (maxCopyCount > m_receiveBuffer.Size)
                            maxCopyCount = m_receiveBuffer.Size;

                        // Copy the buffer data to the packet buffer
                        Buffer.BlockCopy(m_receiveBuffer.Buffer, 0, m_currentBuffer.Buffer, m_currentBuffer.Offset,
                            maxCopyCount);

                        // Update how many bytes we now have
                        m_currentBuffer.Offset += maxCopyCount;
                        m_receiveBuffer.Size -= maxCopyCount;

                        // If there is data remaining in the buffer, copy it over the data
                        // we just removed (sliding buffer).
                        if (m_receiveBuffer.Size > 0)
                            Buffer.BlockCopy(m_receiveBuffer.Buffer, maxCopyCount, m_receiveBuffer.Buffer, 0,
                                m_receiveBuffer.Size);

                        // Check to see if the current packet is now complete.
                        if (m_currentBuffer.Size == m_currentBuffer.Offset)
                        {
                            // If so, dispatch it to the manager class for processing by the system.
                            m_currentBuffer.Offset = 0;
                            incomingBuffersTmp.Add(m_currentBuffer);

                            // Set the current packet to null so we can process the next packet
                            // in the stream.
                            m_currentBuffer = null;
                        }
                        else
                            break;
                    }
                }

                // iterate the buffers if there's any
                if (incomingBuffersTmp.Count > 0)
                {
                    foreach (var buffer in incomingBuffersTmp)
                    {
                        var packetSize = CheckForPrefixAndGetPacketSize(buffer);

                        // todo: check if we have multiple packets in one buffer.
                        var packet = new Packet((ushort) ((buffer.Buffer[4] << 8) | buffer.Buffer[5]), buffer.Buffer, 0,
                            packetSize, m_portIdentifier) {IsIncoming = isIncoming};

                        packet.Lock();

                        // crc check on the packet, if it fails, dont add it.
                        if (ValidateIncomingPacket(packet))
                            m_incomingPackets.Add(packet);
                    }
                }
            }
        }

        private static int CheckForPrefixAndGetPacketSize(TransferBuffer buffer)
        {
            var prefixAr = new byte[7];
            Buffer.BlockCopy(buffer.Buffer, 0, prefixAr, 0, 7);
            var prefix = Encoding.ASCII.GetString(prefixAr);
            if (prefix.Contains("ICOM"))
            {
                var oldBuffer = buffer.Buffer;
                buffer.Buffer = new byte[oldBuffer.Length - 7];
                Buffer.BlockCopy(oldBuffer, 7, buffer.Buffer, 0, buffer.Buffer.Length);
                return ((buffer.Buffer[8] << 8) | buffer.Buffer[9]) + buffer.Buffer[3] + sizeof(int);
            }

            return ((buffer.Buffer[8] << 8) | buffer.Buffer[9]) + buffer.Buffer[3] + sizeof(int);
        }

        private static bool ValidateIncomingPacket(Packet packet)
        {
            try
            {
                var packetSize = packet.GetHeaderLength() + packet.GetMessageLength() + sizeof (int);
                var checkSumOffset = packetSize - sizeof (int);
                var bytes = packet.GetBytes();
                var theirChecksum = (uint)((bytes[checkSumOffset] << 24) | (bytes[checkSumOffset + 1] << 16) |
                                     (bytes[checkSumOffset + 2] << 8) | bytes[checkSumOffset + 3]);

                var bytesWithoutChecksum = new byte[checkSumOffset];
                Buffer.BlockCopy(bytes, 0, bytesWithoutChecksum, 0, checkSumOffset);
                var ourChecksum = Crc32Algorithm.Compute(bytesWithoutChecksum);

                return ourChecksum == theirChecksum;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        ///     Transfers raw incoming data into the adapter object.
        /// </summary>
        public void Receive(byte[] buffer, int offset, int length, bool isIncoming = true)
        {
            Receive(new TransferBuffer(buffer, offset, length, true), isIncoming);
        }

        public void Send(Packet packet)
        {
            lock (m_classLock)
            {
                m_outgoingPackets.Enqueue(packet);
            }
        }
    }
}