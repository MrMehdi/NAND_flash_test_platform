﻿'COPYRIGHT EMBEDDED COMPUTERS LLC 2019 - ALL RIGHTS RESERVED
'THIS SOFTWARE IS ONLY FOR USE WITH GENUINE FLASHCATUSB PRODUCTS
'CONTACT EMAIL: contact@embeddedcomputers.net
'ANY USE OF THIS CODE MUST ADHERE TO THE LICENSE FILE INCLUDED WITH THIS SDK
'INFO: This is the main module that is loaded first.

Imports FlashcatUSB.FlashMemory
Imports FlashcatUSB.FlashcatSettings
Imports FlashcatUSB.MemoryInterface
Imports FlashcatUSB.USB.HostClient
Imports Microsoft.Win32
Imports System.Runtime.InteropServices
Imports System.Threading
Imports FlashcatUSB.SPI
Imports FlashcatUSB.USB

Public Module MainApp
    Public Property RM As Resources.ResourceManager = My.Resources.english.ResourceManager
    Public GUI As MainForm
    Public MySettings As New FlashcatSettings
    Public Const Build As Integer = 576

    Private Const PRO_PCB4_FW As Single = 1.14F 'This is the embedded firmware version for pro
    Private Const PRO_PCB5_FW As Single = 1.02F 'This is the embedded firmware version for pro
    Private Const MACH1_PCB2_FW As Single = 2.08F 'Firmware version for Mach1
    Private Const XPORT_PCB1_FW As Single = 4.48F 'XPORT PCB 1.x
    Private Const XPORT_PCB2_FW As Single = 5.04F 'XPORT PCB 2.x
    Private Const CLASSIC_FW As Single = 4.47F 'Min revision allowed for classic (PCB 2.x)

    Private Const CPLD_SPI_3V3 As UInt32 = &HCD330002UI 'ID CODE FOR SPI (3.3v)
    Private Const CPLD_SPI_1V8 As UInt32 = &HCD180002UI 'ID CODE FOR JTAG (1.8v)
    Private Const CPLD_I2C As UInt32 = &HBD330002UI 'ID CODE FOR I2C (3.3v)
    Private Const CPLD_JTAG As UInt32 = &HCD330101UI 'ID CODE FOR JTAG (3.3v)
    Private Const CPLD_QSPI_3V3 As UInt32 = &HCD330121UI 'ID CODE FOR JTAG (3.3v)
    Private Const CPLD_QSPI_1V8 As UInt32 = &HCD180121UI 'ID CODE FOR JTAG (1.8v)
    Private Const CPLD_NAND_1V8 As UInt32 = &HAD180002UI 'ID CODE FOR NAND_X8 (1.8v)
    Private Const CPLD_NAND_3V3 As UInt32 = &HAD330002UI 'ID CODE FOR NAND_X8 (3.3v)
    Private Const CPLD_HF_1V8 As UInt32 = &HAD180101UI 'ID CODE FOR HF (1.8v)
    Private Const CPLD_HF_3V3 As UInt32 = &HAD330101UI 'ID CODE FOR HF (3.3v)
    Private Const MACH1_FGPA_3V3 As UInt32 = &HAF330004UI
    Private Const MACH1_FGPA_1V8 As UInt32 = &HAF180004UI
    Public AppIsClosing As Boolean = False
    Public FlashDatabase As New FlashDatabase 'This contains definitions of all of the supported Flash devices
    Public WithEvents ScriptEngine As New FcScriptEngine
    Public WithEvents MEM_IF As New MemoryInterface 'Contains API to access the various memory devices
    Public WithEvents USBCLIENT As New HostClient
    Public ScriptPath As String = Application.StartupPath & "\Scripts\" 'Holds the full directory name of where scripts are located
    Public Platform As String
    Public CUSTOM_SPI_DEV As SPI_NOR
    Private FcMutex As Mutex
    Public IS_DEBUG_VER As Boolean = False

    Sub Main(ByVal Args() As String)
        Try 'This makes it only allow one instance
            Dim created As Boolean = False
            FcMutex = New Mutex(False, "FCUSB", created)
            If Not FcMutex.WaitOne(0, False) Then
                FcMutex = Nothing
                Exit Sub
            End If
        Catch ex As Exception
            Exit Sub
        End Try

        'Args = {"-WRITE", "-SPI", "-FILE", "test.bin", "-offset", "0"}
        'Args = {"-READ", "-SPI", "-MHZ", "24", "-FILE", "Flash.bin", "-LENGTH", "5000000"}
        'Args = {"-READ", "-SPINAND", "-MHZ", "32", "-FILE", "Flash.bin", "-LENGTH", "20000000", "-LOG", "temp.txt"}
        'Args = {"-READ", "-SPIEEPROM", "-EEPROM", "M95M02", "-FILE", "Flash.bin"}
        'Args = {"-WRITE", "-SPI", "-FILE", "Flash.bin"}
        'Args = {"-EXECUTE", "-SPI", "-FILE", "sample.fcs"}

        Thread.CurrentThread.CurrentUICulture = Globalization.CultureInfo.CreateSpecificCulture("en-US")
        Thread.CurrentThread.CurrentCulture = Globalization.CultureInfo.CreateSpecificCulture("en-US")
        My.Application.ChangeUICulture("en-US")
        My.Application.ChangeCulture("en-US")
        If CUSTOM_SPI_DEV Is Nothing Then CUSTOM_SPI_DEV = New SPI_NOR("User-defined", VCC_IF.SPI_3V, Mb001, 0, 0)
        CreateGrayCodeTable()
        Create_SPI_EEPROM_List() 'Adds the SPI EEPROM devices
        ECC_LoadSettings()
        Thread.CurrentThread.Name = "rootApp"
        Platform = My.Computer.Info.OSFullName & " (" & GetOsBitsString() & ")"
        If Args IsNot Nothing AndAlso Args.Count > 0 Then 'We are running as CONSOLE
            RunConsoleMode(Args)
        Else 'We are running normal GUI
            GUI = New MainForm
            AddHandler ScriptEngine.WriteConsole, AddressOf WriteConsole
            AddHandler ScriptEngine.SetStatus, AddressOf SetStatus
            USBCLIENT.StartService()
            Application.Run(GUI)
        End If
        AppClosing()
    End Sub

    Public Class WriteParameters
        Public Address As Long = 0 'flash address to write to
        Public BytesLeft As Long = 0 'Number of bytes to write from this stream
        Public BytesWritten As Long = 0 'Number of bytes we have written
        Public BytesTotal As Long = 0 'The total number of bytes to write
        Public Status As New MemoryDeviceInstance.StatusCallback 'Contains all the delegates (if connected)
        Public Memory_Area As FlashArea = FlashArea.Main 'Indicates the sub area we want to write to
        Public Timer As Stopwatch 'To monitor the transfer speed
        'Write Specific Parameters:
        Public EraseSector As Boolean = True  'True if we want to erase each sector prior to write
        Public Verify As Boolean = True 'True if we want to read back the data
        Public AbortOperation As Boolean = False
    End Class

    Public Class ReadParameters
        Public Address As Long = 0
        Public Count As Long = 0
        Public Status As New MemoryDeviceInstance.StatusCallback 'Contains all the delegates (if connected)
        Public Memory_Area As FlashArea = FlashArea.Main 'Indicates the sub area we want to read
        Public Timer As Stopwatch 'To monitor the transfer speed
        Public AbortOperation As Boolean = False
    End Class

#Region "Error correcting code"
    Public NAND_ECC_ENG As ECC_LIB.Engine

    Public Sub ECC_LoadSettings()
        NAND_ECC_ENG = New ECC_LIB.Engine(MySettings.ECC_Algorithum, MySettings.ECC_BitError)
        NAND_ECC_ENG.ECC_DATA_LOCATION = MySettings.ECC_Location
        NAND_ECC_ENG.ECC_SEPERATE = MySettings.ECC_Separate
        NAND_ECC_ENG.REVERSE_ARRAY = MySettings.ECC_Reverse
        NAND_ECC_ENG.SetSymbolWidth(MySettings.ECC_SymWidth)
    End Sub

#End Region

#Region "Bit Swapping / Endian Feature, and Gray Code tables (for JTAG)"

    Public Enum BitSwapMode
        None = 0
        Bits_8 = 1 '0x01 = 0x80
        Bits_16 = 2 '0x0102 = 0x4080
        Bits_32 = 3 '0x00010203 = 0x20C04080
    End Enum

    Public Enum BitEndianMode
        BigEndian32 = 0 '0x01020304 = 0x01020304 (default)
        BigEndian16 = 1 '0x01020304 = 0x02010403
        LittleEndian32_8bit = 2 '0x01020304 = 0x03040102
        LittleEndian32_16bit = 3 '0x01020304 = 0x02010403
    End Enum
    'FILE-->MEMORY
    Public Sub BitSwap_Forward(ByRef data() As Byte)
        Select Case MySettings.BIT_ENDIAN
            Case BitEndianMode.BigEndian16
                Utilities.ChangeEndian16_MSB(data)
            Case BitEndianMode.LittleEndian32_8bit
                Utilities.ChangeEndian32_LSB8(data)
            Case BitEndianMode.LittleEndian32_16bit
                Utilities.ChangeEndian32_LSB16(data)
        End Select
        Select Case MySettings.BIT_SWAP
            Case BitSwapMode.Bits_8
                Utilities.ReverseBits_Byte(data)
            Case BitSwapMode.Bits_16
                Utilities.ReverseBits_HalfWord(data)
            Case BitSwapMode.Bits_32
                Utilities.ReverseBits_Word(data)
        End Select
    End Sub
    'MEMORY-->FILE
    Public Sub BitSwap_Reverse(ByRef data() As Byte)
        Select Case MySettings.BIT_SWAP
            Case BitSwapMode.Bits_8
                Utilities.ReverseBits_Byte(data)
            Case BitSwapMode.Bits_16
                Utilities.ReverseBits_HalfWord(data)
            Case BitSwapMode.Bits_32
                Utilities.ReverseBits_Word(data)
        End Select
        Select Case MySettings.BIT_ENDIAN
            Case BitEndianMode.BigEndian16
                Utilities.ChangeEndian16_MSB(data)
            Case BitEndianMode.LittleEndian32_8bit
                Utilities.ChangeEndian32_LSB8(data)
            Case BitEndianMode.LittleEndian32_16bit
                Utilities.ChangeEndian32_LSB16(data)
        End Select
    End Sub
    'Number of bytes needed
    Public Function BitSwap_Offset() As Integer
        Dim bits_needed As Integer = 0
        Select Case MySettings.BIT_SWAP
            Case BitSwapMode.Bits_16
                bits_needed = 2
            Case BitSwapMode.Bits_32
                bits_needed = 4
        End Select
        Select Case MySettings.BIT_ENDIAN
            Case BitEndianMode.BigEndian16
                bits_needed = 4
            Case BitEndianMode.LittleEndian32_16bit
                bits_needed = 4
            Case BitEndianMode.LittleEndian32_8bit
                bits_needed = 4
        End Select
        Return bits_needed
    End Function

    Public gray_code_table_reverse(255) As Byte
    Public gray_code_table(255) As Byte

    Public Sub CreateGrayCodeTable()
        For i = 0 To 255
            Dim data_in() As Byte = {(i >> 1) Xor i}
            gray_code_table(i) = data_in(0)
            Utilities.ReverseBits_Byte(data_in)
            gray_code_table_reverse(i) = data_in(0)
        Next
    End Sub

#End Region

#Region "SPI EEPROM"
    Public SPI_EEPROM_LIST As New List(Of SPI_NOR)

    Public Enum SPI_EEPROM As Byte
        None = 0 'User must select SPI EEPROM device
        nRF24LE1 = 1 '16384 bytes
        nRF24LU1P_16KB = 2 '16384 bytes
        nRF24LU1P_32KB = 3 '32768 bytes
        AT25010A = 4 '128 bytes
        AT25020A = 5 '256 bytes
        AT25040A = 6  '512 bytes
        AT25080 = 7 '1024 bytes
        AT25160 = 8 '2048 bytes
        AT25320 = 9 '4096 bytes
        AT25640 = 10 '8192 bytes
        AT25128B = 11 '16384 bytes
        AT25256B = 12 '32768 bytes
        AT25512 = 13 '65536 bytes
        M95010 = 14 '128 bytes
        M95020 = 15  '256 bytes
        M95040 = 16 '512 bytes
        M95080 = 17 '1024 bytes
        M95160 = 18 '2048 bytes
        M95320 = 19 '4096 bytes
        M95640 = 20 '8192 bytes
        M95128 = 21 '16384 bytes
        M95256 = 22 '32768 bytes
        M95512 = 23  '65536 bytes
        M95M01 = 24 '131072 bytes
        M95M02 = 25 '262144 bytes
        M25AA512 = 26 'Microchip 64 bytes
        M25AA160A = 27 '2048 bytes
        M25AA160B = 28 '2048 bytes
    End Enum

    Public Sub Create_SPI_EEPROM_List()
        Dim nRF24LE1 As New SPI_NOR("Nordic nRF24LE1", VCC_IF.SPI_3V, 16384, 0, 0) With {.EEPROM = SPI_EEPROM.nRF24LE1, .PAGE_SIZE = 512}
        nRF24LE1.OP_COMMANDS.SE = &H52
        nRF24LE1.ERASE_SIZE = 512
        nRF24LE1.ADDRESSBITS = 16
        nRF24LE1.ProgramMode = SPI_ProgramMode.Nordic
        SPI_EEPROM_LIST.Add(nRF24LE1)
        Dim nRF24LU1_16KB As New SPI_NOR("Nordic nRF24LU1+ (16KB)", VCC_IF.SPI_3V, 16384, 0, 0) With {.EEPROM = SPI_EEPROM.nRF24LU1P_16KB, .PAGE_SIZE = 256}
        nRF24LU1_16KB.OP_COMMANDS.SE = &H52
        nRF24LU1_16KB.ERASE_SIZE = 512
        nRF24LU1_16KB.ADDRESSBITS = 16
        nRF24LU1_16KB.ProgramMode = SPI_ProgramMode.Nordic
        SPI_EEPROM_LIST.Add(nRF24LU1_16KB)
        Dim nRF24LU1_32KB As New SPI_NOR("Nordic nRF24LU1+ (32KB)", VCC_IF.SPI_3V, 32768, 0, 0) With {.EEPROM = SPI_EEPROM.nRF24LU1P_32KB, .PAGE_SIZE = 256}
        nRF24LU1_32KB.OP_COMMANDS.SE = &H52
        nRF24LU1_32KB.ERASE_SIZE = 512
        nRF24LU1_32KB.ADDRESSBITS = 16
        nRF24LU1_32KB.ProgramMode = SPI_ProgramMode.Nordic
        SPI_EEPROM_LIST.Add(nRF24LU1_32KB)
        Dim AT25010A As New SPI_NOR("Atmel AT25010A", VCC_IF.SPI_3V, 128, 0, 0) With {.EEPROM = SPI_EEPROM.AT25010A, .PAGE_SIZE = 8}
        AT25010A.ADDRESSBITS = 8 'check
        AT25010A.ERASE_REQUIRED = False 'We will not send erase commands
        AT25010A.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25010A)
        Dim AT25020A As New SPI_NOR("Atmel AT25020A", VCC_IF.SPI_3V, 256, 0, 0) With {.EEPROM = SPI_EEPROM.AT25020A, .PAGE_SIZE = 8}
        AT25020A.ADDRESSBITS = 8
        AT25020A.ERASE_REQUIRED = False 'We will not send erase commands
        AT25020A.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25020A)
        Dim AT25040A As New SPI_NOR("Atmel AT25040A", VCC_IF.SPI_3V, 512, 0, 0) With {.EEPROM = SPI_EEPROM.AT25040A, .PAGE_SIZE = 8}
        AT25040A.ADDRESSBITS = 8
        AT25040A.ERASE_REQUIRED = False 'We will not send erase commands
        AT25040A.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25040A)
        Dim AT25080 As New SPI_NOR("Atmel AT25080", VCC_IF.SPI_3V, 1024, 0, 0) With {.EEPROM = SPI_EEPROM.AT25080, .PAGE_SIZE = 8}
        AT25080.ADDRESSBITS = 16
        AT25080.ERASE_REQUIRED = False 'We will not send erase commands
        AT25080.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25080)
        Dim AT25160 As New SPI_NOR("Atmel AT25160", VCC_IF.SPI_3V, 2048, 0, 0) With {.EEPROM = SPI_EEPROM.AT25160, .PAGE_SIZE = 32}
        AT25160.ADDRESSBITS = 16
        AT25160.ERASE_REQUIRED = False 'We will not send erase commands
        AT25160.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25160)
        Dim AT25320 As New SPI_NOR("Atmel AT25320", VCC_IF.SPI_3V, 4096, 0, 0) With {.EEPROM = SPI_EEPROM.AT25320, .PAGE_SIZE = 32}
        AT25320.ADDRESSBITS = 16
        AT25320.ERASE_REQUIRED = False 'We will not send erase commands
        AT25320.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25320)
        Dim AT25640 As New SPI_NOR("Atmel AT25640", VCC_IF.SPI_3V, 8192, 0, 0) With {.EEPROM = SPI_EEPROM.AT25640, .PAGE_SIZE = 32}
        AT25640.ADDRESSBITS = 16
        AT25640.ERASE_REQUIRED = False 'We will not send erase commands
        AT25640.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25640)
        Dim AT25128B As New SPI_NOR("Atmel AT25128B", VCC_IF.SPI_3V, 16384, 0, 0) With {.EEPROM = SPI_EEPROM.AT25128B, .PAGE_SIZE = 64}
        AT25128B.ADDRESSBITS = 16
        AT25128B.ERASE_REQUIRED = False 'We will not send erase commands
        AT25128B.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25128B)
        Dim AT25256B As New SPI_NOR("Atmel AT25256B", VCC_IF.SPI_3V, 32768, 0, 0) With {.EEPROM = SPI_EEPROM.AT25256B, .PAGE_SIZE = 64}
        AT25256B.ADDRESSBITS = 16
        AT25256B.ERASE_REQUIRED = False 'We will not send erase commands
        AT25256B.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25256B)
        Dim AT25512 As New SPI_NOR("Atmel AT25512", VCC_IF.SPI_3V, 65536, 0, 0) With {.EEPROM = SPI_EEPROM.AT25512, .PAGE_SIZE = 128}
        AT25512.ADDRESSBITS = 16
        AT25512.ERASE_REQUIRED = False 'We will not send erase commands
        AT25512.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(AT25512)
        Dim M95010 As New SPI_NOR("ST M95010", VCC_IF.SPI_3V, 128, 0, 0) With {.EEPROM = SPI_EEPROM.M95010, .PAGE_SIZE = 16}
        M95010.ADDRESSBITS = 8
        M95010.ERASE_REQUIRED = False 'We will not send erase commands
        M95010.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95010)
        Dim M95020 As New SPI_NOR("ST M95020", VCC_IF.SPI_3V, 256, 0, 0) With {.EEPROM = SPI_EEPROM.M95020, .PAGE_SIZE = 16}
        M95020.ADDRESSBITS = 8
        M95020.ERASE_REQUIRED = False 'We will not send erase commands
        M95020.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95020)
        Dim M95040 As New SPI_NOR("ST M95040", VCC_IF.SPI_3V, 512, 0, 0) With {.EEPROM = SPI_EEPROM.M95040, .PAGE_SIZE = 16}
        M95040.ADDRESSBITS = 8
        M95040.ERASE_REQUIRED = False 'We will not send erase commands
        M95040.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95040)
        Dim M95080 As New SPI_NOR("ST M95080", VCC_IF.SPI_3V, 1024, 0, 0) With {.EEPROM = SPI_EEPROM.M95080, .PAGE_SIZE = 32}
        M95080.ADDRESSBITS = 16
        M95080.ERASE_REQUIRED = False 'We will not send erase commands
        M95080.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95080)
        Dim M95160 As New SPI_NOR("ST M95160", VCC_IF.SPI_3V, 2048, 0, 0) With {.EEPROM = SPI_EEPROM.M95160, .PAGE_SIZE = 32}
        M95160.ADDRESSBITS = 16
        M95160.ERASE_REQUIRED = False 'We will not send erase commands
        M95160.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95160)
        Dim M95320 As New SPI_NOR("ST M95320", VCC_IF.SPI_3V, 4096, 0, 0) With {.EEPROM = SPI_EEPROM.M95320, .PAGE_SIZE = 32}
        M95320.ADDRESSBITS = 16
        M95320.ERASE_REQUIRED = False 'We will not send erase commands
        M95320.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95320)
        Dim M95640 As New SPI_NOR("ST M95640", VCC_IF.SPI_3V, 8192, 0, 0) With {.EEPROM = SPI_EEPROM.M95640, .PAGE_SIZE = 32}
        M95640.ADDRESSBITS = 16
        M95640.ERASE_REQUIRED = False 'We will not send erase commands
        M95640.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95640)
        Dim M95128 As New SPI_NOR("ST M95128", VCC_IF.SPI_3V, 16384, 0, 0) With {.EEPROM = SPI_EEPROM.M95128, .PAGE_SIZE = 64}
        M95128.ADDRESSBITS = 16
        M95128.ERASE_REQUIRED = False 'We will not send erase commands
        M95128.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95128)
        Dim M95256 As New SPI_NOR("ST M95256", VCC_IF.SPI_3V, 32768, 0, 0) With {.EEPROM = SPI_EEPROM.M95256, .PAGE_SIZE = 64}
        M95256.ADDRESSBITS = 16
        M95256.ERASE_REQUIRED = False 'We will not send erase commands
        M95256.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95256)
        Dim M95512 As New SPI_NOR("ST M95512", VCC_IF.SPI_3V, 65536, 0, 0) With {.EEPROM = SPI_EEPROM.M95512, .PAGE_SIZE = 128}
        M95512.ADDRESSBITS = 16
        M95512.ERASE_REQUIRED = False 'We will not send erase commands
        M95512.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95512)
        Dim M95M01 As New SPI_NOR("ST M95M01", VCC_IF.SPI_3V, 131072, 0, 0) With {.EEPROM = SPI_EEPROM.M95M01, .PAGE_SIZE = 256}
        M95M01.ADDRESSBITS = 24
        M95M01.ERASE_REQUIRED = False 'We will not send erase commands
        M95M01.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95M01)
        Dim M95M02 As New SPI_NOR("ST M95M02", VCC_IF.SPI_3V, 262144, 0, 0) With {.EEPROM = SPI_EEPROM.M95M02, .PAGE_SIZE = 256}
        M95M02.ADDRESSBITS = 24
        M95M02.ERASE_REQUIRED = False 'We will not send erase commands
        M95M02.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M95M02)
        Dim MC25AA512 As New SPI_NOR("Microchip 25AA512", VCC_IF.SPI_3V, 65536, 0, 0) With {.EEPROM = SPI_EEPROM.M25AA512, .PAGE_SIZE = 128}
        MC25AA512.ADDRESSBITS = 16
        MC25AA512.ERASE_REQUIRED = False 'We will not send erase commands
        MC25AA512.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(MC25AA512)
        Dim M25AA160A As New SPI_NOR("Microchip 25AA160A", VCC_IF.SPI_3V, 2048, 0, 0) With {.EEPROM = SPI_EEPROM.M25AA160A, .PAGE_SIZE = 16}
        M25AA160A.ADDRESSBITS = 16
        M25AA160A.ERASE_REQUIRED = False 'We will not send erase commands
        M25AA160A.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M25AA160A)
        Dim M25AA160B As New SPI_NOR("Microchip 25AA160B", VCC_IF.SPI_3V, 2048, 0, 0) With {.EEPROM = SPI_EEPROM.M25AA160B, .PAGE_SIZE = 32}
        M25AA160B.ADDRESSBITS = 16
        M25AA160B.ERASE_REQUIRED = False 'We will not send erase commands
        M25AA160B.ProgramMode = SPI_ProgramMode.SPI_EEPROM
        SPI_EEPROM_LIST.Add(M25AA160B)
    End Sub

    Public Function Get_SPI_EEPROM(ByVal dev As SPI_EEPROM) As SPI_NOR
        For Each spi_dev In SPI_EEPROM_LIST
            If spi_dev.EEPROM = dev Then Return spi_dev
        Next
        Return Nothing
    End Function

    Public Sub SPIEEPROM_Configure(ByVal usb_dev As FCUSB_DEVICE, ByVal eeprom As SPI_EEPROM)
        Dim nRF24_mode As Boolean = False
        usb_dev.SPI_NOR_IF.SPIBUS_Setup()
        usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, 1000000))
        Select Case eeprom
            Case SPI_EEPROM.nRF24LE1  '16384 bytes
                usb_dev.USB_VCC_3V() 'Ignored by PCB 2.x
                nRF24_mode = True
            Case SPI_EEPROM.nRF24LU1P_16KB   '16384 bytes
                usb_dev.USB_VCC_3V() 'Ignored by PCB 2.x
                nRF24_mode = True
            Case SPI_EEPROM.nRF24LU1P_32KB   '32768 bytes
                usb_dev.USB_VCC_3V() 'Ignored by PCB 2.x
                nRF24_mode = True
        End Select
        If nRF24_mode Then
            Utilities.Sleep(100)
            usb_dev.SPI_NOR_IF.SetProgPin(True) 'Sets PROG.PIN to HIGH
            usb_dev.SPI_NOR_IF.SetProgPin(False) 'Sets PROG.PIN to LOW
            usb_dev.SPI_NOR_IF.SetProgPin(True) 'Sets PROG.PIN to HIGH
            Utilities.Sleep(10)
            If (USBCLIENT.HW_MODE = FCUSB_BOARD.Professional_PCB4) Then
                usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, 12000000))
            ElseIf (USBCLIENT.HW_MODE = FCUSB_BOARD.Professional_PCB5) Then
                usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, 1000000))
            ElseIf (USBCLIENT.HW_MODE = FCUSB_BOARD.XPORT_PCB1) Then
                usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, 2000000))
            ElseIf (USBCLIENT.HW_MODE = FCUSB_BOARD.XPORT_PCB2) Then
                usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, 2000000))
            Else
                usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, 8000000))
            End If
        End If
        usb_dev.SPI_NOR_IF.MyFlashDevice = Get_SPI_EEPROM(MySettings.SPI_EEPROM)
        usb_dev.SPI_NOR_IF.MyFlashStatus = DeviceStatus.Supported
        If eeprom = SPI_EEPROM.M95010 Then
            usb_dev.SPI_NOR_IF.WriteStatusRegister({0}) 'Disable BP0/BP1
        ElseIf eeprom = SPI_EEPROM.M95020 Then
            usb_dev.SPI_NOR_IF.WriteStatusRegister({0}) 'Disable BP0/BP1
        ElseIf eeprom = SPI_EEPROM.M95040 Then
            usb_dev.SPI_NOR_IF.WriteStatusRegister({0}) 'Disable BP0/BP1
        ElseIf eeprom = SPI_EEPROM.M95080 Then
            usb_dev.SPI_NOR_IF.WriteStatusRegister({0}) 'Disable BP0/BP1
        End If
    End Sub

#End Region

#Region "Console mode"
    Declare Function AllocConsole Lib "kernel32" () As Integer
    Declare Function FreeConsole Lib "kernel32" () As Integer
    Private ConsoleLog As New List(Of String)
    Private MyConsoleOperation As New ConsoleOperation
    Private SEL_USB_PATH As String = ""

    Private Class ConsoleOperation
        Public Property CurrentTask As ConsoleTask = ConsoleTask.NoTask
        Public Property Mode As DeviceMode = DeviceMode.Unspecified
        Public Property VERIFY As Boolean = True
        Public Property CHIP_ERASE As Boolean = False 'Erase the entire device before writing data
        Public Property FILENAME As String 'The filename to write to or read from
        Public Property FILE_IO As IO.FileInfo
        Public Property FILE_LENGTH As Integer = 0 'Optional file length argument
        Public Property FLASH_OFFSET As UInt32 = 0
        Public Property FLASH_SIZE As UInt32 = 0
        Public Property SPI_EEPROM As SPI_NOR = Nothing
        Public Property SPI_SPEED As Integer = 8 'In MHZ
        Public Property I2C_EEPROM As I2C_Programmer.I2C_DEVICE = Nothing
        Public Property LogOutput As Boolean = False
        Public Property LogAppendFile As Boolean = False
        Public Property LogFilename As String = "FlashcatUSB_Console.txt"
        Public Property ExitConsole As Boolean = False 'Closes the console window when complete

    End Class

    Public Sub RunConsoleMode(args() As String)
        Dim mem_dev As MemoryDeviceInstance = Nothing
        If Not Convert.ToBoolean(AllocConsole()) Then Exit Sub
        ConsoleWriteLine(String.Format(RM.GetString("welcome_to_flashcatusb") & ", Build: {0}", Build))
        ConsoleWriteLine("Copyright " & DateTime.Now.Year & " - Embedded Computers LLC")
        ConsoleWriteLine(String.Format("Running on: {0}", Platform))
        Environment.ExitCode = 0
        If (args Is Nothing OrElse args.Length = 0) Then
            Console_DisplayHelp()
            Console_Exit()
            Exit Sub
        End If
        Select Case args(0).ToUpper
            Case "-H", "-?", "-HELP"
                MyConsoleOperation.CurrentTask = ConsoleTask.Help
            Case "-LISTPATHS"
                MyConsoleOperation.CurrentTask = ConsoleTask.Path
            Case "-READ"
                MyConsoleOperation.CurrentTask = ConsoleTask.ReadMemory
            Case "-WRITE"
                MyConsoleOperation.CurrentTask = ConsoleTask.WriteMemory
            Case "-ERASE"
                MyConsoleOperation.CurrentTask = ConsoleTask.EraseMemory
            Case "-EXECUTE"
                MyConsoleOperation.CurrentTask = ConsoleTask.ExecuteScript
            Case Else
                ConsoleWriteLine(RM.GetString("console_operation_not_specified"))
                Console_Exit() : Exit Sub
        End Select
        If MyConsoleOperation.CurrentTask = ConsoleTask.Help Or MyConsoleOperation.CurrentTask = ConsoleTask.Path Then
        ElseIf args.Length = 1 Then
            Console_DisplayHelp()
            Console_Exit() : Exit Sub
        Else
            Select Case args(1).ToUpper
                Case "-SPI"
                    MyConsoleOperation.Mode = DeviceMode.SPI
                Case "-SPIEEPROM"
                    MyConsoleOperation.Mode = DeviceMode.SPI_EEPROM
                Case "-SPINAND"
                    MyConsoleOperation.Mode = DeviceMode.SPI_NAND
                Case "-I2C"
                    MyConsoleOperation.Mode = DeviceMode.I2C_EEPROM
                Case "-EXTIO"
                    MyConsoleOperation.Mode = DeviceMode.NOR_NAND
                Case "-JTAG"
                    MyConsoleOperation.Mode = DeviceMode.JTAG
                Case "-SWI"
                    MyConsoleOperation.Mode = DeviceMode.SINGLE_WIRE
                Case Else
                    Environment.ExitCode = -1
                    ConsoleWriteLine(RM.GetString("console_mode_not_specified"))
                    Console_Exit() : Exit Sub
            End Select
        End If
        If Not Console_LoadOptions(args) Then Console_Exit() : Exit Sub
        Select Case MyConsoleOperation.CurrentTask
            Case ConsoleTask.Help
                Console_DisplayHelp()
                Console_Exit() : Exit Sub
            Case ConsoleTask.Path
                Console_DisplayPaths()
                Console_Exit() : Exit Sub
            Case ConsoleTask.ReadMemory
                If (MyConsoleOperation.FILENAME = "") Then
                    Environment.ExitCode = -1
                    ConsoleWriteLine(RM.GetString("console_readmem_req")) : Console_Exit() : Exit Sub
                End If
                MyConsoleOperation.FILE_IO = New IO.FileInfo(MyConsoleOperation.FILENAME)
            Case ConsoleTask.WriteMemory
                If (MyConsoleOperation.FILENAME = "") Then
                    Environment.ExitCode = -1
                    ConsoleWriteLine(RM.GetString("console_writemem_req")) : Console_Exit() : Exit Sub
                End If
                MyConsoleOperation.FILE_IO = New IO.FileInfo(MyConsoleOperation.FILENAME)
                If Not MyConsoleOperation.FILE_IO.Exists Then
                    Environment.ExitCode = -1
                    ConsoleWriteLine(RM.GetString("err_file_not_found") & ": " & MyConsoleOperation.FILENAME) : Console_Exit() : Exit Sub
                End If
            Case ConsoleTask.ExecuteScript
                If MyConsoleOperation.FILENAME = "" Then
                    Environment.ExitCode = -1
                    ConsoleWriteLine(RM.GetString("console_exescript_req")) : Console_Exit() : Exit Sub
                End If
                MyConsoleOperation.FILE_IO = New IO.FileInfo(Application.StartupPath & "\Scripts\" & MyConsoleOperation.FILENAME)
                If Not MyConsoleOperation.FILE_IO.Exists Then
                    MyConsoleOperation.FILE_IO = New IO.FileInfo(MyConsoleOperation.FILENAME)
                    If Not MyConsoleOperation.FILE_IO.Exists Then
                        Environment.ExitCode = -1
                        ConsoleWriteLine(RM.GetString("err_file_not_found") & ": " & MyConsoleOperation.FILE_IO.FullName) : Console_Exit() : Exit Sub
                    End If
                End If
                AddHandler ScriptEngine.WriteConsole, AddressOf ConsoleWriteLine
        End Select
        Dim usb_dev As FCUSB_DEVICE = USBCLIENT.Connect(SEL_USB_PATH)
        If usb_dev Is Nothing Then
            Environment.ExitCode = -1
            ConsoleWriteLine(RM.GetString("err_unable_to_connect"))
            Console_Exit() : Exit Sub
        End If
        USBCLIENT.FCUSB(0) = usb_dev
        usb_dev.USB_LEDOn()
        ConsoleWriteLine(RM.GetString("successfully_connected")) '"Successfully connected to FlashcatUSB over USB"
        ConsoleWriteLine(RM.GetString("board_fw_version") & ": " & usb_dev.FW_VERSION) 'Board firmware version
        If (usb_dev.HWBOARD = FCUSB_BOARD.Professional_PCB4) Then
            If Not FCUSBPRO_PCB4_Init(usb_dev) Then
                ConsoleWriteLine("Failed to initialize board")
                Console_Exit() : Exit Sub
            End If
            FCUSBPRO_SetDeviceVoltage(usb_dev, True)
        ElseIf (usb_dev.HWBOARD = FCUSB_BOARD.Professional_PCB5) Then
            If Not FCUSBPRO_PCB5_Init(usb_dev) Then
                ConsoleWriteLine("Failed to initialize board")
                Console_Exit() : Exit Sub
            End If
            FCUSBPRO_SetDeviceVoltage(usb_dev, True)
        End If
        Select Case MyConsoleOperation.Mode
            Case DeviceMode.SPI
                ConsoleWriteLine(RM.GetString("device_mode") & ": Serial Programmable Interface (SPI-NOR)")
                If usb_dev.SPI_NOR_IF.DeviceInit() Then
                    MyConsoleOperation.FLASH_SIZE = usb_dev.SPI_NOR_IF.MyFlashDevice.FLASH_SIZE
                    mem_dev = MEM_IF.Add(usb_dev, usb_dev.SPI_NOR_IF.MyFlashDevice)
                    Dim desired_speed As UInt32 = (MyConsoleOperation.SPI_SPEED * 1000000)
                    usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, desired_speed))
                    ConsoleWriteLine(String.Format(RM.GetString("spi_set_clock"), MyConsoleOperation.SPI_SPEED) & " MHz")
                Else
                    Select Case usb_dev.SPI_NOR_IF.MyFlashStatus
                        Case DeviceStatus.NotDetected
                            ConsoleWriteLine(RM.GetString("spi_not_detected"))
                        Case DeviceStatus.NotSupported
                            ConsoleWriteLine(RM.GetString("spi_unable_detect"))
                    End Select
                    Environment.ExitCode = -1
                    Console_Exit() : Exit Sub
                End If 'Console mode
            Case DeviceMode.SPI_NAND
                ConsoleWriteLine(RM.GetString("device_mode") & ": Serial Programmable Interface (SPI-NAND)")
                If usb_dev.SPI_NAND_IF.DeviceInit() Then
                    MyConsoleOperation.FLASH_SIZE = usb_dev.SPI_NAND_IF.MyFlashDevice.FLASH_SIZE
                    mem_dev = MEM_IF.Add(usb_dev, usb_dev.SPI_NAND_IF.MyFlashDevice)
                    Dim desired_speed As UInt32 = (MyConsoleOperation.SPI_SPEED * 1000000)
                    usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, desired_speed))
                    ConsoleWriteLine(String.Format(RM.GetString("spi_set_clock"), MyConsoleOperation.SPI_SPEED) & " MHz")
                Else
                    Select Case usb_dev.SPI_NOR_IF.MyFlashStatus
                        Case DeviceStatus.NotDetected
                            ConsoleWriteLine(RM.GetString("spi_nand_unable_to_detect"))
                        Case DeviceStatus.NotSupported
                            ConsoleWriteLine(RM.GetString("mem_not_supported"))
                    End Select
                    Environment.ExitCode = -1
                    Console_Exit() : Exit Sub
                End If
            Case DeviceMode.SPI_EEPROM
                ConsoleWriteLine(RM.GetString("device_mode") & ": Serial Programmable Interface (SPI-EEPROM)")
                usb_dev.SPI_NOR_IF.SPIBUS_Setup()
                SPIEEPROM_Configure(usb_dev, MyConsoleOperation.SPI_EEPROM.EEPROM)
                MyConsoleOperation.FLASH_SIZE = MyConsoleOperation.SPI_EEPROM.FLASH_SIZE
                mem_dev = MEM_IF.Add(usb_dev, MyConsoleOperation.SPI_EEPROM)
                mem_dev.PreferredBlockSize = 1024
            Case DeviceMode.I2C_EEPROM
                MyConsoleOperation.FLASH_SIZE = MyConsoleOperation.I2C_EEPROM.Size
                usb_dev.I2C_IF.DeviceInit() 'Initiates the I2C engine
                If usb_dev.I2C_IF.IsConnected() Then
                    ConsoleWriteLine(RM.GetString("device_mode") & ": Inter-Integrated Circuit (I²C)")
                Else
                    ConsoleWriteLine(RM.GetString("i2c_unable_to_connect"))
                    Environment.ExitCode = -1
                    Console_Exit() : Exit Sub
                End If
            Case DeviceMode.NOR_NAND
                ConsoleWriteLine(RM.GetString("device_mode") & ": Extension Port Interface")
                usb_dev.EXT_IF.DeviceInit()
                Select Case usb_dev.EXT_IF.MyFlashStatus
                    Case DeviceStatus.Supported
                        Dim device_id As String = Hex(usb_dev.EXT_IF.MyFlashDevice.MFG_CODE).PadLeft(2, "0") & " " & Hex(usb_dev.EXT_IF.MyFlashDevice.ID1).PadLeft(4, "0")
                        ConsoleWriteLine(RM.GetString("device_mode") & ": Multi-purpose Flash device (CHIP ID: " & device_id & ")")
                        mem_dev = MEM_IF.Add(usb_dev, usb_dev.EXT_IF.MyFlashDevice)
                        mem_dev.PreferredBlockSize = 32768
                        MyConsoleOperation.FLASH_SIZE = usb_dev.EXT_IF.DeviceSize
                    Case DeviceStatus.NotSupported
                        ConsoleWriteLine(RM.GetString("mem_not_supported"))
                        Environment.ExitCode = -1
                        Console_Exit() : Exit Sub
                    Case DeviceStatus.ExtIoNotConnected
                        ConsoleWriteLine(RM.GetString("ext_board_not_detected"))
                        Environment.ExitCode = -1
                        Console_Exit() : Exit Sub
                    Case DeviceStatus.NotDetected
                        ConsoleWriteLine(RM.GetString("ext_not_detected"))
                        Environment.ExitCode = -1
                        Console_Exit() : Exit Sub
                End Select
            Case DeviceMode.JTAG
                ConsoleWriteLine(RM.GetString("device_mode") & ": JTAG")
                If (Not usb_dev.JTAG_IF.Init) Then
                    ConsoleWriteLine(RM.GetString("jtag_failed_to_connect"))
                    Environment.ExitCode = -1
                    Console_Exit() : Exit Sub
                Else
                    ConsoleWriteLine(RM.GetString("jtag_setup"))
                End If
                If Not usb_dev.JTAG_IF.Detected Then
                    ConsoleWriteLine(RM.GetString("jtag_no_idcode"))
                    Environment.ExitCode = -1
                    Console_Exit() : Exit Sub
                End If
            Case DeviceMode.SINGLE_WIRE
                If usb_dev.SWI_IF.DeviceInit Then
                    ConsoleWriteLine(RM.GetString("device_mode") & ": Single-wire EEPROM (SWI)")
                Else
                    ConsoleWriteLine("Unable to connect to SWI EEPROM")
                    Environment.ExitCode = -1
                    Console_Exit() : Exit Sub
                End If
                MyConsoleOperation.FLASH_SIZE = usb_dev.SWI_IF.DeviceSize
        End Select
        ConsoleProgressReset = True
        If MyConsoleOperation.CurrentTask = ConsoleTask.ExecuteScript Then
        ElseIf MyConsoleOperation.Mode = DeviceMode.I2C_EEPROM Then
        ElseIf MyConsoleOperation.Mode = DeviceMode.SINGLE_WIRE Then
        Else
            If (MEM_IF.DeviceCount = 0) Then
                Environment.ExitCode = -1
                ConsoleWriteLine(RM.GetString("console_no_mem_devices")) : Console_Exit() : Exit Sub
            End If
        End If
        Select Case MyConsoleOperation.CurrentTask
            Case ConsoleTask.ReadMemory
                If (MyConsoleOperation.FLASH_OFFSET > MyConsoleOperation.FLASH_SIZE) Then MyConsoleOperation.FLASH_OFFSET = 0 'Out of bounds
                If (MyConsoleOperation.FILE_LENGTH = 0) Or ((MyConsoleOperation.FLASH_OFFSET + MyConsoleOperation.FILE_LENGTH) > MyConsoleOperation.FLASH_SIZE) Then
                    MyConsoleOperation.FILE_LENGTH = (MyConsoleOperation.FLASH_SIZE - MyConsoleOperation.FLASH_OFFSET)
                End If
                Dim data_to_read() As Byte = Nothing
                If (MyConsoleOperation.Mode = DeviceMode.I2C_EEPROM) Then
                    ConsoleWriteLine(String.Format(RM.GetString("console_i2c_reading"), MyConsoleOperation.I2C_EEPROM.Name, Format(MyConsoleOperation.I2C_EEPROM.Size, "#,###")))
                    ConsoleWriteLine(String.Format(RM.GetString("console_i2c_params"), Hex(MySettings.I2C_ADDRESS), Hex(MyConsoleOperation.FLASH_OFFSET), Format(MyConsoleOperation.FILE_LENGTH, "#,###")))
                    data_to_read = usb_dev.I2C_IF.ReadData(MyConsoleOperation.FLASH_OFFSET, MyConsoleOperation.FILE_LENGTH)
                ElseIf (MyConsoleOperation.Mode = DeviceMode.SINGLE_WIRE) Then
                    ConsoleWriteLine(String.Format("Reading data from SWI Flash device: {0} ({1} bytes)", usb_dev.SWI_IF.DeviceName, Format(usb_dev.SWI_IF.DeviceSize, "#,###")))
                    ConsoleWriteLine(String.Format("SWI parameters: offset: 0x{0}, length: {1} bytes", Hex(MyConsoleOperation.FLASH_OFFSET), Format(MyConsoleOperation.FILE_LENGTH, "#,###")))
                    data_to_read = usb_dev.SWI_IF.ReadData(MyConsoleOperation.FLASH_OFFSET, MyConsoleOperation.FILE_LENGTH)
                Else
                    Dim cb As New MemoryDeviceInstance.StatusCallback
                    cb.UpdatePercent = New UpdateFunction_Progress(AddressOf Console_UpdateProgress)
                    cb.UpdateSpeed = New UpdateFunction_SpeedLabel(AddressOf Console_UpdateSpeed)
                    data_to_read = mem_dev.ReadBytes(MyConsoleOperation.FLASH_OFFSET, MyConsoleOperation.FILE_LENGTH, FlashArea.Main, cb)
                End If
                If data_to_read Is Nothing OrElse data_to_read.Length = 0 Then
                    Environment.ExitCode = -1
                    ConsoleWriteLine(RM.GetString("console_read_err_nodata")) : Console_Exit() : Exit Sub
                End If
                Utilities.FileIO.WriteBytes(data_to_read, MyConsoleOperation.FILE_IO.FullName)
                Dim console_data_saved As String = "Saved data to: {0}" 'Move this to translated files
                ConsoleWriteLine(String.Format(console_data_saved, MyConsoleOperation.FILE_IO.FullName))
            Case ConsoleTask.WriteMemory
                If (MyConsoleOperation.FLASH_OFFSET > MyConsoleOperation.FLASH_SIZE) Then MyConsoleOperation.FLASH_OFFSET = 0 'Out of bounds
                Dim max_write_count As UInt32 = Math.Min(MyConsoleOperation.FLASH_SIZE, MyConsoleOperation.FILE_IO.Length)
                If (MyConsoleOperation.FILE_LENGTH = 0) Then
                    MyConsoleOperation.FILE_LENGTH = max_write_count
                ElseIf MyConsoleOperation.FILE_LENGTH > max_write_count Then
                    MyConsoleOperation.FILE_LENGTH = max_write_count
                End If
                Dim data_out() As Byte = Utilities.FileIO.ReadBytes(MyConsoleOperation.FILE_IO.FullName, MyConsoleOperation.FILE_LENGTH)
                If (MyConsoleOperation.Mode = DeviceMode.I2C_EEPROM) Then
                    If data_out Is Nothing OrElse data_out.Length = 0 Then
                        Environment.ExitCode = -1
                        ConsoleWriteLine(RM.GetString("console_write_err_nodata")) : Console_Exit() : Exit Sub
                    End If
                    ReDim Preserve data_out(MyConsoleOperation.FILE_LENGTH - 1)
                    ConsoleWriteLine(String.Format(RM.GetString("console_i2c_writing"), MyConsoleOperation.I2C_EEPROM.Name, Format(MyConsoleOperation.I2C_EEPROM.Size, "#,###")))
                    ConsoleWriteLine(String.Format(RM.GetString("console_i2c_params"), Hex(MySettings.I2C_ADDRESS), Hex(MyConsoleOperation.FLASH_OFFSET), Format(MyConsoleOperation.FILE_LENGTH, "#,###")))
                    If usb_dev.I2C_IF.WriteData(MyConsoleOperation.FLASH_OFFSET, data_out) Then
                        ConsoleWriteLine(RM.GetString("console_i2c_write_success"))
                    Else
                        ConsoleWriteLine(RM.GetString("console_i2c_write_error"))
                    End If
                ElseIf (MyConsoleOperation.Mode = DeviceMode.SINGLE_WIRE) Then
                    If data_out Is Nothing OrElse data_out.Length = 0 Then
                        Environment.ExitCode = -1
                        ConsoleWriteLine(RM.GetString("console_write_err_nodata")) : Console_Exit() : Exit Sub
                    End If
                    ConsoleWriteLine(String.Format("Writing data to SWI Flash device: {0} ({1} bytes)", usb_dev.SWI_IF.DeviceName, Format(usb_dev.SWI_IF.DeviceSize, "#,###")))
                    ConsoleWriteLine(String.Format("SWI parameters: offset: 0x{0}, length: {1} bytes", Hex(MyConsoleOperation.FLASH_OFFSET), Format(MyConsoleOperation.FILE_LENGTH, "#,###")))
                    If usb_dev.SWI_IF.WriteData(MyConsoleOperation.FLASH_OFFSET, data_out) Then
                        ConsoleWriteLine("SWI EEPROM write was successful")
                    Else
                        ConsoleWriteLine("Error: unable to write to SWI EEPROM device")
                    End If
                Else
                    If MyConsoleOperation.CHIP_ERASE Then
                        ConsoleWriteLine(RM.GetString("mem_erasing_device"))
                        mem_dev.EraseFlash()
                    End If
                    If data_out Is Nothing OrElse data_out.Length = 0 Then
                        Environment.ExitCode = -1
                        ConsoleWriteLine(RM.GetString("console_write_err_nodata")) : Console_Exit() : Exit Sub
                    End If
                    Dim verify_str As String = "enabled"
                    If Not MyConsoleOperation.VERIFY Then verify_str = "disabled"

                    ConsoleWriteLine("Performing WRITE of " & MyConsoleOperation.FILE_LENGTH & " bytes at offset 0x" &
                                     Hex(MyConsoleOperation.FLASH_OFFSET) & " with verify " & verify_str)

                    ReDim Preserve data_out(MyConsoleOperation.FILE_LENGTH - 1)
                    Dim cb As New MemoryDeviceInstance.StatusCallback
                    cb.UpdatePercent = New UpdateFunction_Progress(AddressOf Console_UpdateProgress)
                    cb.UpdateSpeed = New UpdateFunction_SpeedLabel(AddressOf Console_UpdateSpeed)
                    Dim write_result As Boolean = mem_dev.WriteBytes(MyConsoleOperation.FLASH_OFFSET, data_out, FlashArea.Main, MyConsoleOperation.VERIFY, cb)
                    If write_result Then
                        ConsoleWriteLine(RM.GetString("mem_write_successful"))
                    Else
                        ConsoleWriteLine(RM.GetString("mem_write_not_successful"))
                    End If
                End If
            Case ConsoleTask.EraseMemory
                ConsoleWriteLine(RM.GetString("mem_erase_device"))
                Try
                    If mem_dev.EraseFlash() Then
                        ConsoleWriteLine(RM.GetString("mem_erase_device_success"))
                    Else
                        ConsoleWriteLine(RM.GetString("mem_erase_device_fail"))
                    End If
                Catch ex As Exception
                    ConsoleWriteLine(RM.GetString("mem_erase_device_fail"))
                End Try
            Case ConsoleTask.ExecuteScript
                ScriptEngine.CURRENT_DEVICE_MODE = MyConsoleOperation.Mode
                If (Not ScriptEngine.LoadFile(MyConsoleOperation.FILE_IO)) Then
                    Environment.ExitCode = -1
                    Console_Exit() : Exit Sub
                Else
                    Do While ScriptEngine.IsRunning
                        Application.DoEvents()
                        Utilities.Sleep(20)
                    Loop
                End If
        End Select
        ConsoleWriteLine("----------------------------------------------")
        ConsoleWriteLine(RM.GetString("console_complete"))
        If MyConsoleOperation.LogOutput Then
            If MyConsoleOperation.LogAppendFile Then
                Utilities.FileIO.AppendFile(ConsoleLog.ToArray, MyConsoleOperation.LogFilename)
            Else
                Utilities.FileIO.WriteFile(ConsoleLog.ToArray, MyConsoleOperation.LogFilename)
            End If
        End If
        Console_Exit()
        usb_dev.USB_LEDOff()
        usb_dev.Disconnect()
    End Sub

    Private Function Console_LoadOptions(ByVal Args() As String) As Boolean
        Dim option_task As New List(Of String)
        For i = 2 To Args.Count - 1
            option_task.Add(Args(i))
        Next
        Do Until option_task.Count = 0
            Dim name As String = option_task(0).ToUpper
            option_task.RemoveAt(0) 'Pop the stack
            Select Case name
                Case "-PATH" 'User is requesting a specific device 
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "PATH")) : Console_Exit() : Return False
                    End If
                    SEL_USB_PATH = option_task(0) : option_task.RemoveAt(0) 'Add option and pop
                    ConsoleWriteLine("USB path set to: " & SEL_USB_PATH)
                Case "-ERASE"
                    If (Not MyConsoleOperation.CurrentTask = ConsoleTask.WriteMemory) Then
                        ConsoleWriteLine(RM.GetString("console_erase_not_valid")) : Console_Exit() : Return False
                    End If
                    MyConsoleOperation.CHIP_ERASE = True
                Case "-FILE"
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "FILE")) : Console_Exit() : Return False
                    End If
                    MyConsoleOperation.FILENAME = option_task(0) : option_task.RemoveAt(0)
                Case "-LOG"
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "LOG")) : Console_Exit() : Return False
                    End If
                    MyConsoleOperation.LogFilename = option_task(0) : option_task.RemoveAt(0)
                    MyConsoleOperation.LogOutput = True
                Case "-MHZ"
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "MHZ")) : Console_Exit() : Return False
                    End If
                    Dim speed_val As String = option_task(0) : option_task.RemoveAt(0)
                    If IsNumeric(speed_val) AndAlso (speed_val > 1 OrElse speed_val < 48) Then
                        MyConsoleOperation.SPI_SPEED = CInt(speed_val)
                    Else
                        ConsoleWriteLine("MHZ value must be between 1 and 48") : Console_Exit() : Return False
                    End If
                Case "-LOGAPPEND"
                    MyConsoleOperation.LogAppendFile = True
                Case "-OFFSET"
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "OFFSET")) : Console_Exit() : Return False
                    End If
                    Dim offset_value As String = option_task(0) : option_task.RemoveAt(0)
                    If (Not Utilities.IsDataType.HexString(offset_value)) AndAlso (Not IsNumeric(offset_value)) Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_numeric_hex"), "OFFSET")) : Console_Exit() : Return False
                    End If
                    Try
                        If IsNumeric(offset_value) Then
                            MyConsoleOperation.FLASH_OFFSET = CUInt(offset_value)
                        ElseIf Utilities.IsDataType.HexString(offset_value) Then
                            MyConsoleOperation.FLASH_OFFSET = Utilities.HexToUInt(offset_value)
                        End If
                    Catch ex As Exception
                    End Try
                Case "-LENGTH"
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "LENGTH")) : Console_Exit() : Return False
                    End If
                    Dim offset_value As String = option_task(0) : option_task.RemoveAt(0)
                    If (Not Utilities.IsDataType.HexString(offset_value)) AndAlso (Not IsNumeric(offset_value)) Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_numeric_hex"), "LENGTH")) : Console_Exit() : Return False
                    End If
                    Try
                        If IsNumeric(offset_value) Then
                            MyConsoleOperation.FILE_LENGTH = CUInt(offset_value)
                        ElseIf Utilities.IsDataType.HexString(offset_value) Then
                            MyConsoleOperation.FILE_LENGTH = Utilities.HexToUInt(offset_value)
                        End If
                    Catch ex As Exception
                    End Try
                Case "-VERIFY_OFF"
                    MyConsoleOperation.VERIFY = False
                Case "-EXIT"
                    MyConsoleOperation.ExitConsole = True
                Case "-ADDRESS"
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "ADDRESS")) : Console_Exit() : Return False
                    End If
                    Dim offset_value As String = option_task(0) : option_task.RemoveAt(0)
                    If (Not Utilities.IsDataType.HexString(offset_value)) AndAlso (Not IsNumeric(offset_value)) Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_numeric_hex"), "ADDRESS")) : Console_Exit() : Return False
                    End If
                    Try
                        If IsNumeric(offset_value) Then
                            MySettings.I2C_ADDRESS = CByte(CUInt(offset_value) And 255)
                        ElseIf Utilities.IsDataType.HexString(offset_value) Then
                            MySettings.I2C_ADDRESS = CByte(Utilities.HexToUInt(offset_value) And 255)
                        End If
                    Catch ex As Exception
                    End Try
                Case "-EEPROM"
                    If option_task.Count = 0 OrElse option_task(0).StartsWith("-") Then
                        ConsoleWriteLine(String.Format(RM.GetString("console_value_missing"), "EEPROM")) : Console_Exit() : Return False
                        Console_ListEEPROMs()
                        Return False
                    End If
                    Dim eeprom_str As String = option_task(0) : option_task.RemoveAt(0)
                    Dim Device_Found As Boolean = False
                    For Each dev In USBCLIENT.FCUSB(0).I2C_IF.I2C_EEPROM_LIST
                        If eeprom_str.ToUpper = dev.Name.ToUpper Then
                            MyConsoleOperation.I2C_EEPROM = dev
                            Device_Found = True
                            Exit For
                        End If
                    Next
                    If (Not Device_Found) Then
                        For Each dev In SPI_EEPROM_LIST
                            Dim spi_part As String = dev.NAME.Substring(dev.NAME.IndexOf(" ") + 1)
                            If eeprom_str.ToUpper = spi_part.ToUpper Then
                                MyConsoleOperation.SPI_EEPROM = dev
                                Device_Found = True
                                Exit For
                            End If
                        Next
                    End If
                    If Not Device_Found Then
                        ConsoleWriteLine(RM.GetString("console_eeprom_not_specified"))
                        Console_ListEEPROMs()
                        Return False
                    End If
                Case Else
                    ConsoleWriteLine(String.Format(RM.GetString("console_opt_not_valid"), name))
            End Select
        Loop
        Return True
    End Function
    'Prints the list of valid options that can be used for the -EEPROM option
    Private Sub Console_ListEEPROMs()
        ConsoleWriteLine("I2C/SPI EEPROM valid options are:")
        ConsoleWriteLine("[I2C EEPROM DEVICES]")
        For Each dev In USBCLIENT.FCUSB(0).I2C_IF.I2C_EEPROM_LIST
            ConsoleWriteLine(dev.Name)
        Next
        ConsoleWriteLine("[SPI EEPROM DEVICES]")
        For Each dev In SPI_EEPROM_LIST
            Dim spi_part As String = dev.NAME.Substring(dev.NAME.IndexOf(" ") + 1)
            ConsoleWriteLine(spi_part)
        Next
    End Sub

    Private Sub Console_Exit()
        If MyConsoleOperation.ExitConsole Then
        Else
            ConsoleWriteLine("----------------------------------------------")
            ConsoleWriteLine("Press any key to close")
            Console.ReadKey()
            If MyConsoleOperation.LogOutput Then
                If MyConsoleOperation.LogAppendFile Then
                    Utilities.FileIO.AppendFile(ConsoleLog.ToArray, MyConsoleOperation.LogFilename)
                Else
                    Utilities.FileIO.WriteFile(ConsoleLog.ToArray, MyConsoleOperation.LogFilename)
                End If
            End If
        End If
        FreeConsole() 'DLL CALL
    End Sub

    Public Sub ConsoleWriteLine(ByVal Line As String)
        Try
            Console.WriteLine(Line)
            ConsoleLog.Add(Line)
            Application.DoEvents()
        Catch ex As Exception
        End Try
    End Sub

    Private Enum ConsoleTask
        NoTask
        Help
        Path
        ReadMemory
        WriteMemory
        EraseMemory
        ExecuteScript
    End Enum

    Public Property ConsoleProgressReset As Boolean = False
    Private Delegate Sub UpdateFunction_Progress(ByVal percent As Integer)
    Private Delegate Sub UpdateFunction_SpeedLabel(ByVal speed_str As String)

    Private Sub Console_UpdateProgress(percent As Integer)
        Try
            If percent > 100 Then percent = 100
            If ConsoleProgressReset Then
                Console.WriteLine("")
                ConsoleProgressReset = False
            End If
            Console.SetCursorPosition(0, Console.CursorTop - 1)
            Console.Write(RM.GetString("console_progress"), percent.ToString.PadLeft(3, " "))
            Console.SetCursorPosition(0, Console.CursorTop + 1)
        Catch ex As Exception
        End Try
    End Sub

    Private Sub Console_UpdateSpeed(speed_str As String)
        Try
            If ConsoleProgressReset Then
                Console.WriteLine("")
                ConsoleProgressReset = False
            End If
            Console.SetCursorPosition(15, Console.CursorTop - 1)
            Console.Write(" [" & speed_str & "]          ")
            Console.SetCursorPosition(0, Console.CursorTop + 1)
        Catch ex As Exception
        End Try
    End Sub

    Private Sub Console_DisplayHelp()
        Dim running_exe As New IO.FileInfo(Reflection.Assembly.GetExecutingAssembly().Location)
        ConsoleWriteLine("--------------------------------------------")
        ConsoleWriteLine("Syntax: " & running_exe.Name & " [OPERATION] [MODE] (options) ...")
        ConsoleWriteLine("")
        ConsoleWriteLine("Operations:")
        ConsoleWriteLine("-read             " & RM.GetString("console_opt_read"))
        ConsoleWriteLine("-write            " & RM.GetString("console_opt_write"))
        ConsoleWriteLine("-erase            " & RM.GetString("console_opt_erasechip"))
        ConsoleWriteLine("-execute          " & RM.GetString("console_opt_exe"))
        ConsoleWriteLine("-listpaths        " & RM.GetString("console_opt_list"))
        ConsoleWriteLine("-help             " & RM.GetString("console_opt_help"))
        ConsoleWriteLine("")
        ConsoleWriteLine("Supported modes:")
        ConsoleWriteLine("-SPI -SPIEEPROM -SPINAND -I2C -EXTIO -SWI -JTAG")
        ConsoleWriteLine("")
        ConsoleWriteLine("Options:")
        ConsoleWriteLine("-File (filename)  " & RM.GetString("console_opt_file"))
        ConsoleWriteLine("-Length (value)   " & RM.GetString("console_opt_length"))
        ConsoleWriteLine("-MHZ (value)   " & "Specifies the MHz speed for SPI operation")
        ConsoleWriteLine("-Offset (value)   " & RM.GetString("console_opt_offset"))
        ConsoleWriteLine("-EEPROM (part)    " & RM.GetString("console_opt_eeprom"))
        ConsoleWriteLine("-Address (hex)    " & RM.GetString("console_opt_addr"))
        ConsoleWriteLine("-Erase            " & RM.GetString("console_opt_erase"))
        ConsoleWriteLine("-Path (string)    " & RM.GetString("console_opt_path"))
        ConsoleWriteLine("-Verify_Off       " & RM.GetString("console_opt_verify"))
        ConsoleWriteLine("-Exit             " & RM.GetString("console_opt_exit"))
        ConsoleWriteLine("-Log (filename)   " & RM.GetString("console_opt_log"))
        ConsoleWriteLine("-LogAppend        " & RM.GetString("console_logappend"))
    End Sub

    Private Sub Console_DisplayPaths()
        Dim paths() As String = USBCLIENT.GetConnectedPaths()
        ConsoleWriteLine("--------------------------------------------")
        If paths Is Nothing OrElse paths.Count = 0 Then
            ConsoleWriteLine(RM.GetString("console_no_fcusb"))
        Else
            ConsoleWriteLine(RM.GetString("console_usb_list"))
            Dim i As Integer = 0
            For Each usbdev In paths
                ConsoleWriteLine("Index " & i.ToString & " FlashcatUSB: " & usbdev)
                i += 1
            Next
        End If
    End Sub

#End Region

#Region "SPI Clock Settings"

    Public Function GetSpiClockString(usb_dev As FCUSB_DEVICE) As String
        Dim current_speed As UInt32 = GetSpiClock(usb_dev.HWBOARD, MySettings.SPI_CLOCK_MAX)
        Return (current_speed / 1000000).ToString & " Mhz"
    End Function

    Public Function GetSpiClock(ByVal brd As FCUSB_BOARD, ByVal desired_speed As UInt32) As UInt32
        Dim MCK As UInt32 = 0
        Select Case brd
            Case FCUSB_BOARD.Classic '16MHz MCK
                If (desired_speed > 8000000) Then Return 8000000 'Fastest possible
                MCK = 16000000
            Case FCUSB_BOARD.XPORT_PCB1  '16MHz MCK
                If (desired_speed > 8000000) Then Return 8000000 'Fastest possible
                MCK = 16000000
            Case FCUSB_BOARD.XPORT_PCB2  '16MHz MCK
                If (desired_speed > 8000000) Then Return 8000000 'Fastest possible
                MCK = 16000000
            Case FCUSB_BOARD.Professional_PCB4
                If (desired_speed > 48000000) Then Return 48000000 'Fastest possible
                MCK = 96000000
            Case FCUSB_BOARD.Professional_PCB5
                If (desired_speed > 80000000) Then Return 80000000 'Fastest possible
                MCK = 96000000
            Case FCUSB_BOARD.Mach1
                If (desired_speed > 20000000) Then Return 20000000 'Fastest possible
                MCK = 96000000
            Case Else
                Return 0
        End Select
        Dim spi_baud_div As UInt32 = Math.Ceiling(CSng(MCK) / CSng(desired_speed))
        Dim max_speed As UInt32 = (MCK / spi_baud_div)
        Return max_speed
    End Function

#End Region

    Public Enum MEM_PROTOCOL As Byte
        SETUP = 0
        NOR_X8 = 1
        NOR_X16 = 2
        NOR_X16_X8 = 3
        NAND_X8 = 4
        NAND_X16 = 5
        FWH = 6
        HYPERFLASH = 7
        EPROM_X8 = 8
        EPROM_X16 = 9
    End Enum

    Public Class FlashcatSettings
        Public Property LanguageName As String
        Public Property VOLT_SELECT As Voltage 'Selects output voltage and level
        Public Property OPERATION_MODE As DeviceMode = DeviceMode.SPI
        Public Property VERIFY_WRITE As Boolean 'Read back written data to compare write was successful
        Public Property VERIFY_COUNT As Integer 'Number of times to retry a write operation
        Public Property BIT_ENDIAN As BitEndianMode = BitEndianMode.BigEndian32 'Mirrors bits (not saved)
        Public Property BIT_SWAP As BitSwapMode = BitSwapMode.None 'Swaps nibbles/bytes/words (not saved)
        Public Property MUTLI_NOR As Boolean  'Multiple NOR devices connected in parallel
        Public Property MULTI_CE As Integer 'A18+this value
        'SPI Settings
        Public Property SPI_CLOCK_MAX As UInt32
        Public Property SPI_BIT_ORDER As SPI_ORDER 'MSB/LSB
        Public Property SPI_MODE As SPI_CLOCK_POLARITY 'MODE=0 
        Public Property SPI_EEPROM As SPI_EEPROM
        Public Property SPI_FASTREAD As Boolean
        Public Property SPI_AUTO As Boolean 'Indicates if the software will use common op commands
        Public Property SPI_NAND_DISABLE_ECC As Boolean
        Public Property SPI_QUAD_SPEED As SQI_SPEED
        'I2C Settings
        Public Property I2C_ADDRESS As Byte
        Public Property I2C_SPEED As I2C_SPEED_MODE
        Public Property I2C_INDEX As Int32 'The device selected index


        'Public Property I2C_SIZE As Int32




        'SWI Settings
        Public Property SWI_ADDRESS As Byte 'Slave Address
        'NAND Settings
        Public Property NAND_Preserve As Boolean = True 'We want to copy SPARE data before erase
        Public Property NAND_Verify As Boolean = False
        Public Property NAND_BadBlockManager As BadBlockMode 'Indicates how BAD BLOCKS are detected
        Public Property NAND_BadBlockMarkers As BadBlockMarker
        Public Property NAND_MismatchSkip As Boolean = True
        Public Property NAND_Layout As NandMemLayout = NandMemLayout.Separated
        'NAND ECC Settings
        Public Property ECC_READ_ENABLED As Boolean
        Public Property ECC_WRITE_ENABLED As Boolean
        Public Property ECC_Algorithum As Integer '0=Hamming,1=Reed-Solomon,2=BHC
        Public Property ECC_BitError As Integer 'Number of bits to correct (1,2,4,8,10,14)
        Public Property ECC_Location As Byte
        Public Property ECC_SymWidth As Integer '8/9/10
        Public Property ECC_Separate As Boolean
        Public Property ECC_Reverse As Boolean
        'GENERAL
        Public Property S93_DEVICE_INDEX As Integer 'The index of which Series 93 EEPROM to use
        Public Property S93_DEVICE_ORG As Integer '0=8-bit,1=16-bit
        Public Property SREC_BITMODE As Integer '0=8-bit,1=16-bit
        'JTAG
        Public Property JTAG_SPEED As JTAG_TCK_FREQ
        'License
        Public Property LICENSE_KEY As String
        Public Property LICENSED_TO As String
        Public Property LICENSE_EXP As DateTime

        Sub New()
            LoadLanguageSettings()
            Me.LICENSE_KEY = GetRegistryValue("LICENSE_KEY", "")
            Dim date_str As String = GetRegistryValue("LICENSE_DATE", "01/01/0001")
            Me.LICENSED_TO = GetRegistryValue("LICENSE_NAME", "")
            If date_str.Equals("01/01/0001") Then
                Me.LICENSE_EXP = New DateTime
            Else
                Me.LICENSE_EXP = DateTime.Parse(date_str)
            End If
            Me.MUTLI_NOR = GetRegistryValue("MULTI_NOR", False)
            Me.MULTI_CE = GetRegistryValue("MULTI_CE", 5)
            Me.VOLT_SELECT = GetRegistryValue("VOLTAGE", USB.Voltage.V3_3)
            Me.OPERATION_MODE = CInt(GetRegistryValue("OPERATION", "1")) 'Default is normal
            Me.VERIFY_WRITE = GetRegistryValue("VERIFY", True)
            Me.VERIFY_COUNT = GetRegistryValue("VERIFY_COUNT", 2)
            Me.BIT_ENDIAN = BitEndianMode.BigEndian32
            Me.BIT_SWAP = BitSwapMode.None
            Me.SPI_CLOCK_MAX = GetRegistryValue("SPI_CLOCK_MAX", 10000000)
            Me.SPI_BIT_ORDER = GetRegistryValue("SPI_ORDER", SPI_ORDER.SPI_ORDER_MSB_FIRST)
            Me.SPI_FASTREAD = GetRegistryValue("SPI_FASTREAD", False)
            Me.SPI_BIT_ORDER = GetRegistryValue("SPI_ORDER", SPI_ORDER.SPI_ORDER_MSB_FIRST)
            Me.SPI_MODE = GetRegistryValue("SPI_MODE", SPI_CLOCK_POLARITY.SPI_MODE_0)
            Me.SPI_EEPROM = GetRegistryValue("SPI_EEPROM", SPI_EEPROM.None)
            Me.SPI_AUTO = GetRegistryValue("SPI_AUTO", True)
            Me.SPI_NAND_DISABLE_ECC = GetRegistryValue("SPI_NAND_ECC", False)
            Me.SPI_QUAD_SPEED = GetRegistryValue("SPI_QUAD_SPEED", SQI_SPEED.MHZ_10)
            Me.I2C_ADDRESS = CByte(GetRegistryValue("I2C_ADDR", CInt(&HA0)))
            Me.I2C_SPEED = GetRegistryValue("I2C_SPEED", I2C_SPEED_MODE._400kHz)
            Me.I2C_INDEX = GetRegistryValue("I2C_INDEX", 0)
            Me.SWI_ADDRESS = CByte(GetRegistryValue("SWI_ADDR", CInt(&H0)))
            Me.NAND_Preserve = GetRegistryValue("NAND_Preserve", True)
            Me.NAND_Verify = GetRegistryValue("NAND_Verify", False)
            Me.NAND_BadBlockManager = GetRegistryValue("NAND_BadBlockMode", BadBlockMode.Disabled)
            Me.NAND_BadBlockMarkers = GetRegistryValue("NAND_BadBlockMarker", (BadBlockMarker._1stByte_FirstPage Or BadBlockMarker._1stByte_SecondPage Or BadBlockMarker._1stByte_LastPage))
            Me.NAND_MismatchSkip = GetRegistryValue("NAND_Mismatch", True)
            Me.NAND_Layout = GetRegistryValue("NAND_Layout", NandMemLayout.Separated)
            Me.ECC_READ_ENABLED = GetRegistryValue("ECC_READ", False)
            Me.ECC_WRITE_ENABLED = GetRegistryValue("ECC_WRITE", False)
            Me.ECC_Algorithum = GetRegistryValue("ECC_ALG", 0)
            Me.ECC_BitError = GetRegistryValue("ECC_BITERR", 1)
            Me.ECC_Location = GetRegistryValue("ECC_LOCATION", 2)
            Me.ECC_SymWidth = GetRegistryValue("ECC_SYMWIDTH", 9)
            Me.ECC_Separate = GetRegistryValue("ECC_SEPERATE", True)
            Me.ECC_Reverse = GetRegistryValue("ECC_REVERSE", False)
            Me.S93_DEVICE_INDEX = GetRegistryValue("S93_DEVICE", 0)
            Me.S93_DEVICE_ORG = GetRegistryValue("S93_ORG", 0)
            Me.SREC_BITMODE = GetRegistryValue("SREC_ORG", 0)
            Me.JTAG_SPEED = GetRegistryValue("JTAG_FREQ", JTAG_TCK_FREQ._10MHZ)
            LoadCustomSPI()
        End Sub

        Public Sub Save()
            SetRegistryValue("LICENSE_KEY", Me.LICENSE_KEY)
            SetRegistryValue("LICENSE_NAME", Me.LICENSED_TO)
            SetRegistryValue("LICENSE_DATE", Me.LICENSE_EXP.ToShortDateString)
            SetRegistryValue("MULTI_NOR", Me.MUTLI_NOR)
            SetRegistryValue("MULTI_CE", Me.MULTI_CE)
            SetRegistryValue("VOLTAGE", VOLT_SELECT)
            SetRegistryValue("OPERATION", OPERATION_MODE)
            SetRegistryValue("VERIFY", VERIFY_WRITE)
            SetRegistryValue("VERIFY_COUNT", VERIFY_COUNT)
            SetRegistryValue("ENDIAN", BIT_ENDIAN)
            SetRegistryValue("BITSWAP", BIT_SWAP)
            SetRegistryValue("SPI_CLOCK_MAX", CInt(Me.SPI_CLOCK_MAX))
            SetRegistryValue("SPI_ORDER", SPI_BIT_ORDER)
            SetRegistryValue("SPI_MODE", SPI_MODE)
            SetRegistryValue("SPI_EEPROM", SPI_EEPROM)
            SetRegistryValue("SPI_FASTREAD", SPI_FASTREAD)
            SetRegistryValue("SPI_AUTO", SPI_AUTO)
            SetRegistryValue("SPI_NAND_ECC", SPI_NAND_DISABLE_ECC)
            SetRegistryValue("SPI_QUAD_SPEED", SPI_QUAD_SPEED)
            SetRegistryValue("I2C_ADDR", CInt(I2C_ADDRESS))
            SetRegistryValue("I2C_SPEED", CInt(I2C_SPEED))
            SetRegistryValue("I2C_INDEX", CInt(I2C_INDEX))
            SetRegistryValue("SWI_ADDR", CInt(SWI_ADDRESS))
            SetRegistryValue("NAND_Preserve", NAND_Preserve)
            SetRegistryValue("NAND_Verify", NAND_Verify)
            SetRegistryValue("NAND_BadBlockMode", NAND_BadBlockManager)
            SetRegistryValue("NAND_BadBlockMarker", NAND_BadBlockMarkers)
            SetRegistryValue("NAND_Mismatch", NAND_MismatchSkip)
            SetRegistryValue("NAND_Layout", NAND_Layout)
            SetRegistryValue("Language", LanguageName)
            SetRegistryValue("ECC_READ", Me.ECC_READ_ENABLED)
            SetRegistryValue("ECC_WRITE", Me.ECC_WRITE_ENABLED)
            SetRegistryValue("ECC_ALG", Me.ECC_Algorithum)
            SetRegistryValue("ECC_BITERR", Me.ECC_BitError)
            SetRegistryValue("ECC_LOCATION", Me.ECC_Location)
            SetRegistryValue("ECC_SYMWIDTH", Me.ECC_SymWidth)
            SetRegistryValue("ECC_SEPERATE", Me.ECC_Separate)
            SetRegistryValue("ECC_REVERSE", Me.ECC_Reverse)
            SetRegistryValue("S93_DEVICE", Me.S93_DEVICE_INDEX)
            SetRegistryValue("S93_ORG", Me.S93_DEVICE_ORG)
            SetRegistryValue("SREC_ORG", Me.SREC_BITMODE)
            SetRegistryValue("JTAG_FREQ", Me.JTAG_SPEED)
            SaveCustomSPI()
        End Sub

        Private Sub LoadCustomSPI()
            Try
                Dim data() As Byte = GetRegisteryData("SPI_CUSTOM")
                If data IsNot Nothing AndAlso data.Length > 0 Then
                    Dim ser_data() As Byte = Utilities.DecompressGzip(data)
                    If ser_data IsNot Nothing AndAlso ser_data.Length > 0 Then
                        Using s As New IO.MemoryStream(ser_data)
                            Dim f As New Runtime.Serialization.Formatters.Binary.BinaryFormatter
                            CUSTOM_SPI_DEV = f.Deserialize(s)
                        End Using
                    End If
                End If
            Catch ex As Exception
                CUSTOM_SPI_DEV = Nothing
            End Try
        End Sub

        Private Sub SaveCustomSPI()
            Dim f As New Runtime.Serialization.Formatters.Binary.BinaryFormatter
            Using s As New IO.MemoryStream()
                f.Serialize(s, CUSTOM_SPI_DEV)
                s.Seek(0, IO.SeekOrigin.Begin)
                Dim data_out(s.Length - 1) As Byte
                s.Read(data_out, 0, data_out.Length)
                s.Close()
                If data_out IsNot Nothing AndAlso data_out.Length > 0 Then
                    Dim reg_data() As Byte = Utilities.CompressGzip(data_out)
                    SetRegisteryData("SPI_CUSTOM", reg_data)
                End If
            End Using
        End Sub

        Public Enum BadBlockMode As Integer
            Disabled = 1
            Enabled = 2
        End Enum

        Public Enum BadBlockMarker As Integer
            _1stByte_FirstPage = (1 << 1)
            _1stByte_SecondPage = (1 << 2)
            _1stByte_LastPage = (1 << 3)
            _6thByte_FirstPage = (1 << 4)
            _6thByte_SecondPage = (1 << 5)
        End Enum

        Public Enum NandMemLayout As Integer
            Separated = 1 'We want to see Main or Spare data
            Combined = 2 'We want to see all data 
            Segmented = 3 'Main is spread across the entire page with spare area after each 512 byte chunks
        End Enum

        Public Enum JTAG_TCK_FREQ As Integer
            _10MHZ = 1
            _20MHz = 2
        End Enum

        Public Enum DeviceMode As Byte
            SPI = 1
            JTAG = 2
            I2C_EEPROM = 3
            SPI_EEPROM = 4
            NOR_NAND = 5
            SINGLE_WIRE = 6 '1-Wire
            SPI_NAND = 7
            EPROM = 8 '27-series one-time programmable
            HyperFlash = 9
            Microwire = 10
            SQI = 11
            Unspecified = 20
        End Enum

        Private Sub LoadLanguageSettings()
            Me.LanguageName = GetRegistryValue("Language", "English")
            Select Case Me.LanguageName.ToUpper
                Case "ENGLISH"
                    RM = My.Resources.english.ResourceManager : LanguageName = "English"
                Case "SPANISH"
                    RM = My.Resources.spanish.ResourceManager : LanguageName = "Spanish"
                Case "FRENCH"
                    RM = My.Resources.french.ResourceManager : LanguageName = "French"
                Case "PORTUGUESE"
                    RM = My.Resources.portuguese.ResourceManager : LanguageName = "Portuguese"
                Case "RUSSIAN"
                    RM = My.Resources.russian.ResourceManager : LanguageName = "Russian"
                Case "CHINESE"
                    RM = My.Resources.chinese.ResourceManager : LanguageName = "Chinese"
                Case "ITALIAN"
                    RM = My.Resources.italian.ResourceManager : LanguageName = "Italian"
                Case "GERMAN"
                    RM = My.Resources.german.ResourceManager : LanguageName = "German"
                Case Else
                    RM = My.Resources.english.ResourceManager : LanguageName = "English"
            End Select
        End Sub

        Public Enum I2C_SPEED_MODE As Integer
            _100kHz = 1
            _400kHz = 2
            _1MHz = 3
        End Enum

        Public Sub SetPrefferedScript(ByVal name As String, ByVal id As UInt32)
            SetRegistryValue("SCRIPT_" & id.ToString, name)
        End Sub

        Public Function GetPrefferedScript(ByVal id As UInt32) As String
            Return GetRegistryValue("SCRIPT_" & id.ToString, "")
        End Function

#Region "Registry"
        Private Const REGKEY As String = "Software\EmbComputers\FlashcatUSB\"

        Public Function GetRegistryValue(ByVal Name As String, ByVal DefaultValue As String) As String
            Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY)
            If key Is Nothing Then Return DefaultValue
            Dim o As Object = key.GetValue(Name)
            If o Is Nothing Then Return DefaultValue
            Return CStr(o)
        End Function

        Public Function SetRegistryValue(ByVal Name As String, ByVal Value As String) As Boolean
            Try
                Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY, RegistryKeyPermissionCheck.ReadWriteSubTree)
                key.SetValue(Name, Value)
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        Public Function GetRegistryValue(ByVal Name As String, ByVal DefaultValue As Boolean) As Boolean
            Try
                Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY)
                If key Is Nothing Then Return DefaultValue
                Dim o As Object = key.GetValue(Name)
                If o Is Nothing Then Return DefaultValue
                Return CBool(o)
            Catch ex As Exception
                Return False
            End Try
        End Function

        Public Function SetRegistryValue(ByVal Name As String, ByVal Value As Boolean) As Boolean
            Try
                Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY, RegistryKeyPermissionCheck.ReadWriteSubTree)
                If key Is Nothing Then
                    Registry.CurrentUser.CreateSubKey(REGKEY)
                    key = Registry.CurrentUser.OpenSubKey(REGKEY, True)
                End If
                key.SetValue(Name, Value)
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        Public Function GetRegisteryData(ByVal Name As String) As Byte()
            Try
                Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY)
                If key Is Nothing Then Return Nothing
                Dim o As Object = key.GetValue(Name)
                If o Is Nothing Then Return Nothing
                Return o
            Catch ex As Exception
                Return Nothing
            End Try
        End Function

        Public Function SetRegisteryData(ByVal Name As String, ByVal data() As Byte) As Boolean
            Try
                Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY, RegistryKeyPermissionCheck.ReadWriteSubTree)
                key.SetValue(Name, data)
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        Public Function GetRegistryValue(ByVal Name As String, ByVal DefaultValue As Integer) As Integer
            Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY)
            If key Is Nothing Then Return DefaultValue
            Dim o As Object = key.GetValue(Name)
            If o Is Nothing Then Return DefaultValue
            Return CInt(o)
        End Function

        Public Function SetRegistryValue(ByVal Name As String, ByVal Value As Integer) As Boolean
            Try
                Dim key As RegistryKey = Registry.CurrentUser.OpenSubKey(REGKEY, RegistryKeyPermissionCheck.ReadWriteSubTree)
                key.SetValue(Name, Value)
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

#End Region

    End Class

    Public Function GetCompatibleScripts(ByVal CPUID As UInteger) As String(,)
        Dim Autorun As New IO.FileInfo(ScriptPath & "autorun.ini")
        If Autorun.Exists Then
            Dim autoscripts(,) As String = Nothing
            If ProcessAutorun(Autorun, CPUID, autoscripts) Then
                Return autoscripts
            End If
        End If
        Return Nothing
    End Function

    Public Function ProcessAutorun(ByVal Autorun As IO.FileInfo, ByVal ID As UInteger, ByRef scripts(,) As String) As Boolean
        Try
            Dim f() As String = Utilities.FileIO.ReadFile(Autorun.FullName)
            Dim autoline() As String
            Dim sline As String
            Dim MyCode As UInteger
            Dim out As New ArrayList 'Holds str()
            For Each sline In f
                sline = Trim(Utilities.RemoveComment(sline))
                If Not sline = "" Then
                    autoline = sline.Split(CChar(":"))
                    If autoline.Length = 3 Then
                        MyCode = Utilities.HexToUInt(autoline(0))
                        If MyCode = ID Then
                            out.Add(New String() {autoline(1), autoline(2)})
                        End If
                    End If
                End If
            Next
            If out.Count > 0 Then
                Dim ret(out.Count - 1, 1) As String
                Dim i As Integer
                Dim s() As String
                For i = 0 To out.Count - 1
                    s = CType(out(i), String())
                    ret(i, 0) = s(0)
                    ret(i, 1) = s(1)
                Next
                scripts = ret
                Return True 'Scripts are available
            End If
        Catch ex As Exception
            WriteConsole("Error processing Autorun.ini")
        End Try
        Return False
    End Function

    'Returns the name of the flash device
    Public Function GetDeviceManufacture(ByVal ManuID As Byte) As String
        Select Case ManuID
            Case &H89
                Return "Intel"
            Case &H20
                Return "ST"
            Case &H2C
                Return "Micron"
            Case &H1
                Return "AMD / Spansion"
            Case &H98
                Return "TOSHIBA"
            Case &H4
                Return "FUJITSU"
            Case &HB0
                Return "SHARP"
            Case &HC2
                Return "MXIC"
            Case &H1F
                Return "ATMEL"
            Case &HAD
                Return "HYHYNIX"
            Case &HBF
                Return "SST" 'Silicon Storage
            Case &HEC
                Return "Samsung"
            Case Else
                Return "(Unknown)"
        End Select
    End Function

    'Alternative IO.Compression (for .net 4.0 framework)
    Public Class ZipHelper : Implements IDisposable
        Private zip As IO.Packaging.Package

        Sub New(file As IO.FileInfo)
            Me.FILENAME = file
            If Me.FILENAME.Exists AndAlso Me.FILENAME.Length = 0 Then Me.FILENAME.Delete()
            zip = IO.Packaging.Package.Open(Me.FILENAME.FullName, IO.FileMode.OpenOrCreate)
        End Sub
        'Returns the number of files inside the archive
        Public ReadOnly Property Count As Integer
            Get
                If Not FILENAME.Exists Then Return 0
                Dim p As IO.Packaging.PackagePartCollection = zip.GetParts()
                Return p.Count
            End Get
        End Property
        'Contains the filename of the zip file
        Public ReadOnly Property FILENAME As IO.FileInfo

        Public Sub AddFile(ByVal name As String, ByVal data() As Byte)
            Dim destFilename As String = ".\" & IO.Path.GetFileName(name)
            Dim uri_path As Uri = IO.Packaging.PackUriHelper.CreatePartUri(New Uri(destFilename, UriKind.Relative))
            If (zip.PartExists(uri_path)) Then zip.DeletePart(uri_path)
            Dim part As IO.Packaging.PackagePart = zip.CreatePart(uri_path, "", IO.Packaging.CompressionOption.Normal)
            Using fs As New IO.MemoryStream(data)
                Using dest As IO.Stream = part.GetStream
                    fs.CopyTo(dest)
                End Using
            End Using
        End Sub

        Public Function GetFileData(ByVal name As String) As Byte()
            If Not FILENAME.Exists Then Return Nothing
            Dim destFilename As String = ".\" & IO.Path.GetFileName(name)
            Dim uri_path As Uri = IO.Packaging.PackUriHelper.CreatePartUri(New Uri(destFilename, UriKind.Relative))
            If (Not zip.PartExists(uri_path)) Then Return Nothing 'File not found
            Dim part As IO.Packaging.PackagePart = zip.GetPart(uri_path)
            Using fs As IO.Stream = part.GetStream
                Dim data_out(fs.Length - 1) As Byte
                fs.Read(data_out, 0, data_out.Length)
                Return data_out
            End Using
            Return Nothing
        End Function

        Public Function GetFileStream(ByVal name As String) As IO.Stream
            Try
                If Not FILENAME.Exists Then Return Nothing
                Dim destFilename As String = ".\" & IO.Path.GetFileName(name)
                Dim uri_path As Uri = IO.Packaging.PackUriHelper.CreatePartUri(New Uri(destFilename, UriKind.Relative))
                If (Not zip.PartExists(uri_path)) Then Return Nothing 'File not found
                Dim part As IO.Packaging.PackagePart = zip.GetPart(uri_path)
                Return part.GetStream
            Catch ex As Exception
            End Try
            Return Nothing
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                If zip IsNot Nothing Then
                    zip.Close()
                End If
            Catch ex As Exception
            End Try
        End Sub

        Protected Overrides Sub Finalize()
            Me.Dispose()
        End Sub

    End Class

    <DllImport("kernel32.dll", SetLastError:=True, CallingConvention:=CallingConvention.Winapi)>
    Private Function IsWow64Process(<[In]> hProcess As IntPtr, <Out> ByRef wow64Process As Boolean) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    Public Function GetOsBitsString() As String
        If (Environment.OSVersion.Version.Major = 5 AndAlso Environment.OSVersion.Version.Minor >= 1) OrElse Environment.OSVersion.Version.Major >= 6 Then
            Using p As Process = Process.GetCurrentProcess()
                Dim retVal As Boolean
                If IsWow64Process(p.Handle, retVal) Then
                    If retVal Then Return "64 bit"
                End If
            End Using
        End If
        Return "32 bit"
    End Function

    Public Sub WriteConsole(Msg As String)
        Try
            If AppIsClosing Then Exit Sub
            If GUI IsNot Nothing Then
                GUI.PrintConsole(Msg)
            Else 'We are writing to console
                ConsoleWriteLine(Msg)
                ConsoleProgressReset = True
            End If
        Catch ex As Exception
        End Try
    End Sub

    Public Sub SetStatus(Msg As String)
        If (Not GUI Is Nothing) Then
            GUI.SetStatus(Msg)
        Else
            ConsoleWriteLine("Status update: " & Msg)
            ConsoleProgressReset = True
        End If
    End Sub
    'Returns all of the modes we can support (first one is the default)
    Public Function GetSupportedModes(usb_dev As FCUSB_DEVICE) As DeviceMode()
        Dim modes As New List(Of DeviceMode)
        Select Case usb_dev.HWBOARD
            Case FCUSB_BOARD.Classic
                modes.Add(DeviceMode.SPI)
                modes.Add(DeviceMode.SQI)
                modes.Add(DeviceMode.SPI_NAND)
                modes.Add(DeviceMode.I2C_EEPROM)
                modes.Add(DeviceMode.SPI_EEPROM)
                modes.Add(DeviceMode.Microwire)
                modes.Add(DeviceMode.SINGLE_WIRE)
                modes.Add(DeviceMode.JTAG)
            Case FCUSB_BOARD.XPORT_PCB1, FCUSB_BOARD.XPORT_PCB2
                modes.Add(DeviceMode.NOR_NAND)
                modes.Add(DeviceMode.SPI)
                modes.Add(DeviceMode.SQI)
                modes.Add(DeviceMode.SPI_NAND)
                modes.Add(DeviceMode.I2C_EEPROM)
                modes.Add(DeviceMode.SPI_EEPROM)
                modes.Add(DeviceMode.SPI_NAND)
                modes.Add(DeviceMode.EPROM)
            Case FCUSB_BOARD.Professional_PCB4
                modes.Add(DeviceMode.SPI)
                modes.Add(DeviceMode.JTAG)
                modes.Add(DeviceMode.I2C_EEPROM)
                modes.Add(DeviceMode.SPI_EEPROM)
                modes.Add(DeviceMode.SPI_NAND)
                modes.Add(DeviceMode.SQI)
            Case FCUSB_BOARD.Professional_PCB5
                modes.Add(DeviceMode.SPI)
                modes.Add(DeviceMode.SQI)
                modes.Add(DeviceMode.SPI_NAND)
            Case FCUSB_BOARD.Mach1
                modes.Add(DeviceMode.NOR_NAND)
                modes.Add(DeviceMode.HyperFlash)
                modes.Add(DeviceMode.SQI)
        End Select
        Return modes.ToArray()
    End Function

#Region "USB CONNECTED EVENTS"

    Private Sub OnUsbDevice_Connected(usb_dev As FCUSB_DEVICE) Handles USBCLIENT.DeviceConnected
        usb_dev.USB_LEDOn()
        MEM_IF.Clear(usb_dev) 'Remove all devices that are on this usb port
        If GUI IsNot Nothing Then GUI.SetConnectionStatus(usb_dev)
        OnConnectedEvent(usb_dev)
    End Sub

    Private Sub OnUsbDevice_Disconnected(usb_dev As FCUSB_DEVICE) Handles USBCLIENT.DeviceDisconnected
        usb_dev.USB_LEDOff()
        MEM_IF.Clear(usb_dev) 'Remove all devices that are on this usb port
        If GUI IsNot Nothing Then
            GUI.SetConnectionStatus(usb_dev)
            Dim msg_out As String
            If (USBCLIENT.HW_MODE = FCUSB_BOARD.Professional_PCB4) Then
                msg_out = String.Format(RM.GetString("disconnected_from_device"), "FlashcatUSB Pro") '"Disconnected from FlashcatUSB Pro device"
            ElseIf usb_dev.HWBOARD = FCUSB_BOARD.Mach1 Then
                msg_out = String.Format(RM.GetString("disconnected_from_device"), "FlashcatUSB Mach¹")
            Else
                msg_out = String.Format(RM.GetString("disconnected_from_device"), "FlashcatUSB Classic") '"Disconnected from FlashcatUSB Classic device" 
            End If
            GUI.SetStatus(msg_out)
            WriteConsole(msg_out)
        End If
    End Sub
    'Called whent the device is closing
    Private Sub AppClosing()
        MEM_IF.AbortOperations()
        USBCLIENT.USB_VCC_OFF()
        Application.DoEvents()
        AppIsClosing = True 'Do this last
    End Sub
    'Called when the device connects to USB
    Private Sub OnConnectedEvent(usb_dev As FCUSB_DEVICE)
        usb_dev.USB_Echo() 'This does reset events
        Dim fw_str As String = usb_dev.FW_VERSION()
        If (Not fw_str = "") Then 'Bootloader does not have FW version
            GUI.UpdateStatusMessage(RM.GetString("board_fw_version"), fw_str)
        End If
        Select Case usb_dev.HWBOARD
            Case FCUSB_BOARD.DFU_BL
                GUI.PrintConsole(RM.GetString("connected_bl_mode"))
                DFU_Connected_Event(usb_dev)
                Exit Sub 'No need to detect any device
            Case FCUSB_BOARD.Classic
                GUI.SetStatus("Connected to FlashcatUSB Classic (SPI firmware)")
                If Not FirmwareCheck(fw_str, CLASSIC_FW) Then Exit Sub
                GUI.PrintConsole(String.Format(RM.GetString("connected_fw_ver"), {"FlashcatUSB Classic", fw_str}))
                GUI.PrintConsole(RM.GetString("fw_feat_supported") & ": SPI, I2C, EXTIO")
            Case FCUSB_BOARD.XPORT_PCB1, FCUSB_BOARD.XPORT_PCB2
                GUI.SetStatus("Connected to FlashcatUSB XPORT")
                If usb_dev.HWBOARD = FCUSB_BOARD.XPORT_PCB1 Then
                    If Not FirmwareCheck(fw_str, XPORT_PCB1_FW) Then Exit Sub
                ElseIf usb_dev.HWBOARD = FCUSB_BOARD.XPORT_PCB2 Then
                    If Not FirmwareCheck(fw_str, XPORT_PCB2_FW) Then Exit Sub
                End If
                GUI.PrintConsole(String.Format(RM.GetString("connected_fw_ver"), {"FlashcatUSB XPORT", fw_str}))
                GUI.PrintConsole(RM.GetString("fw_feat_supported") & ": SPI, I2C, EXTIO")
            Case FCUSB_BOARD.Professional_PCB4
                GUI.SetStatus("Connected to FlashcatUSB Professional (PCB 4.x)")
                If usb_dev.USB_IsBootloaderMode() Then
                    If Not (fw_str = "4.02") Then 'outdated bootloader
                        FCUSBPRO_Bootloader(usb_dev, "PCB4_BL_APP.bin") 'Application firmware to update bootloader
                    Else
                        FCUSBPRO_Bootloader(usb_dev, "PCB4_Source.bin") 'Current PCB 4 firmware
                    End If
                    Exit Sub
                ElseIf usb_dev.USB_IsAppUpdaterMode Then
                    FCUSBPRO_Bootloader(usb_dev, "PCB4_BL_1.02.bin") 'This programs the bootloader
                End If
                GUI.PrintConsole(String.Format(RM.GetString("connected_fw_ver"), {"FlashcatUSB Pro", fw_str}))
                Dim AvrVerSng As Single = Utilities.StringToSingle(fw_str)
                If (Not AvrVerSng = PRO_PCB4_FW) Then 'Current firmware is newer or different, do unit update
                    FCUSBPRO_RebootToBootloader(usb_dev)
                    Exit Sub
                End If
            Case FCUSB_BOARD.Professional_PCB5
                GUI.SetStatus("Connected to FlashcatUSB Professional (PCB 5.x)")
                If usb_dev.USB_IsBootloaderMode() Then
                    FCUSBPRO_Bootloader(usb_dev, "PCB5_Source.bin") 'Current PCB5 firmware
                    FCUSBPRO_Bootloader(usb_dev, "PCB5_Source.bin") 'Current PCB5 firmware
                    Exit Sub
                End If
                GUI.PrintConsole(String.Format(RM.GetString("connected_fw_ver"), {"FlashcatUSB Pro", fw_str}))
                Dim AvrVerSng As Single = Utilities.StringToSingle(fw_str)
                If (Not AvrVerSng = PRO_PCB5_FW) Then 'Current firmware is newer or different, do unit update
                    FCUSBPRO_RebootToBootloader(usb_dev)
                    Exit Sub
                End If
            Case FCUSB_BOARD.Mach1 'Designed for high-density/high-speed devices (such as 1Gbit+ NOR/MLC NAND)
                GUI.SetStatus("Connected to FlashcatUSB Mach¹")
                If usb_dev.USB_IsBootloaderMode() Then
                    FCUSBPRO_Bootloader(usb_dev, "Mach1_v2_Source.bin")
                    Exit Sub
                End If
                GUI.PrintConsole(String.Format(RM.GetString("connected_fw_ver"), {"FlashcatUSB Mach¹", fw_str}))
                Dim AvrVerSng As Single = Utilities.StringToSingle(fw_str)
                If Not AvrVerSng = MACH1_PCB2_FW Then
                    FCUSBPRO_RebootToBootloader(usb_dev)
                    Exit Sub
                End If
        End Select
        Dim SupportedModes() As DeviceMode = GetSupportedModes(usb_dev)
        If Array.IndexOf(SupportedModes, MySettings.OPERATION_MODE) = -1 Then
            MySettings.OPERATION_MODE = SupportedModes(0)
        End If
        Select Case usb_dev.HWBOARD
            Case FCUSB_BOARD.Professional_PCB4
                If Not FCUSBPRO_PCB4_Init(usb_dev) Then Exit Sub
            Case FCUSB_BOARD.Professional_PCB5
                If Not FCUSBPRO_PCB5_Init(usb_dev) Then Exit Sub
            Case FCUSB_BOARD.Mach1
                If Not FCUSBPRO_Mach1_Init(usb_dev) Then Exit Sub
        End Select
        FCUSBPRO_SetDeviceVoltage(usb_dev, True)
        If (MySettings.OPERATION_MODE = FlashcatSettings.DeviceMode.SPI) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "Serial Programmable Interface (SPI)")
            DetectDevice(usb_dev)
        ElseIf (MySettings.OPERATION_MODE = FlashcatSettings.DeviceMode.SQI) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "Serial Programmable Interface (SQI)")
            DetectDevice(usb_dev)
        ElseIf (MySettings.OPERATION_MODE = FlashcatSettings.DeviceMode.SPI_EEPROM) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "SPI EEPROM")
            If MySettings.SPI_EEPROM = SPI_EEPROM.None Then
                GUI.SetStatus("Device mode Set To SPI EEPROM, configure SPI settings Then click 'Detect'")
            Else
                DetectDevice(usb_dev)
            End If
        ElseIf (MySettings.OPERATION_MODE = DeviceMode.SPI_NAND) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "Serial Programmable Interface (SPI-NAND)")
            DetectDevice(usb_dev)
        ElseIf (MySettings.OPERATION_MODE = FlashcatSettings.DeviceMode.I2C_EEPROM) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "Inter-Integrated Circuit (I²C)")
            If MySettings.I2C_INDEX = 0 Then
                GUI.SetStatus(RM.GetString("device_mode_i2c")) '"Device mode set to I2C EEPROM, configure I2C settings then click 'Detect'"
            Else
                DetectDevice(usb_dev)
            End If
        ElseIf (MySettings.OPERATION_MODE = DeviceMode.Microwire) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "Microwire (3-wire EEPROM)")
            DetectDevice(usb_dev)
        ElseIf (MySettings.OPERATION_MODE = FlashcatSettings.DeviceMode.NOR_NAND) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "Parallel NOR / NAND mode")
            DetectDevice(usb_dev)
        ElseIf (MySettings.OPERATION_MODE = FlashcatSettings.DeviceMode.EPROM) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "EPROM mode")
            DetectDevice(usb_dev)
        ElseIf (MySettings.OPERATION_MODE = FlashcatSettings.DeviceMode.SINGLE_WIRE) Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "SWI mode")
            DetectDevice(usb_dev)
        ElseIf MySettings.OPERATION_MODE = DeviceMode.JTAG Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "JTAG")
            JTAG_Init(usb_dev)
            If (USBCLIENT.HW_MODE = FCUSB_BOARD.Professional_PCB4) Then
                GUI.SetStatus(String.Format(RM.GetString("jtag_ready"), "FlashcatUSB Pro"))
            ElseIf (USBCLIENT.HW_MODE = FCUSB_BOARD.Professional_PCB5) Then
                GUI.SetStatus(String.Format(RM.GetString("jtag_ready"), "FlashcatUSB Pro"))
            Else
                GUI.SetStatus(String.Format(RM.GetString("jtag_ready"), "FlashcatUSB Classic"))
            End If
        ElseIf MySettings.OPERATION_MODE = DeviceMode.HyperFlash Then
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), "HyperFlash mode")
            DetectDevice(usb_dev)
        End If
    End Sub

    Private Sub JTAG_Init(usb_dev As FCUSB_DEVICE)
        ScriptEngine.CURRENT_DEVICE_MODE = MySettings.OPERATION_MODE
        usb_dev.JTAG_IF.TCK_SPEED = MySettings.JTAG_SPEED 'Supported by Pro only
        If usb_dev.JTAG_IF.Init Then
            GUI.PrintConsole(RM.GetString("jtag_setup"))
        Else
            Dim error_msg As String = RM.GetString("jtag_failed_to_connect")
            GUI.SetStatus(error_msg)
            GUI.UpdateStatusMessage(RM.GetString("device_mode"), error_msg)
            GUI.PrintConsole(error_msg)
            Exit Sub
        End If
        GUI.UpdateStatusMessage(RM.GetString("device_mode"), "JTAG")
        If (usb_dev.JTAG_IF.Count = 1) Then
            If (Not (usb_dev.JTAG_IF.Devices(0).IDCODE = 0)) Then
                Dim mfg_part As String = usb_dev.JTAG_IF.GetJedecDescription(usb_dev.JTAG_IF.Devices(0).IDCODE)
                GUI.UpdateStatusMessage("Device", mfg_part)
                GUI.LoadScripts(usb_dev.JTAG_IF.Devices(0).IDCODE)
            Else
                GUI.PrintConsole(RM.GetString("jtag_no_idcode"))
                GUI.UpdateStatusMessage(RM.GetString("device_mode"), RM.GetString("jtag_unknown_device"))
            End If
        ElseIf (usb_dev.JTAG_IF.Count > 1) Then
            Dim svf_prog As New IO.FileInfo("Scripts\SVF_Player.fcs")
            ScriptEngine.LoadFile(svf_prog)
        End If
    End Sub

    Private Sub DetectDevice(ByVal usb_dev As FCUSB_DEVICE)
        ScriptEngine.CURRENT_DEVICE_MODE = MySettings.OPERATION_MODE
        GUI.SetStatus(RM.GetString("detecting_device"))
        Utilities.Sleep(100) 'Allow time for USB to power up devices
        If MySettings.OPERATION_MODE = DeviceMode.SPI Then
            If MySettings.SPI_AUTO Then
                GUI.PrintConsole(RM.GetString("spi_attempting_detect"))
                If usb_dev.SPI_NOR_IF.DeviceInit Then
                    Dim block_size As UInt32 = 65536
                    If usb_dev.HWBOARD = FCUSB_BOARD.Professional_PCB4 Then block_size = 262144
                    Connected_Event(usb_dev, MemoryType.SERIAL_NOR, "SPI NOR Flash", block_size)
                    GUI.PrintConsole(RM.GetString("spi_detected_spi")) '"Detected SPI Flash on high-speed SPI port"
                    GUI.PrintConsole(String.Format(RM.GetString("spi_set_clock"), GetSpiClockString(usb_dev)))
                    usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, MySettings.SPI_CLOCK_MAX))
                    If (usb_dev.SPI_NOR_IF.W25M121AV_Mode) Then
                        GUI.PrintConsole("Winbond W25M121AV Flash device detected")
                        usb_dev.SPI_NAND_IF.DeviceInit()
                        Connected_Event(usb_dev, MemoryType.SERIAL_NAND, "SPI NAND Flash", 65536)
                    End If
                Else
                    Select Case usb_dev.SPI_NOR_IF.MyFlashStatus
                        Case DeviceStatus.NotDetected
                            GUI.PrintConsole(RM.GetString("spi_unable_detect")) '"Unable to detect to SPI NOR Flash device"
                            GUI.SetStatus(RM.GetString("spi_not_detected")) '"Flash memory not detected on SPI NOR mode"
                        Case DeviceStatus.NotSupported
                            GUI.SetStatus(RM.GetString("mem_not_supported")) '"Flash memory detected but not found in Flash library"
                    End Select
                    Exit Sub
                End If
            Else 'We are using a specified device
                usb_dev.SPI_NOR_IF.SPIBUS_Setup()
                usb_dev.SPI_NOR_IF.MyFlashStatus = DeviceStatus.Supported
                usb_dev.SPI_NOR_IF.MyFlashDevice = CUSTOM_SPI_DEV
                Connected_Event(usb_dev, MemoryType.SERIAL_NOR, "SPI NOR Flash", 65536)
                GUI.PrintConsole(String.Format(RM.GetString("spi_set_clock"), GetSpiClockString(usb_dev)))
                usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, MySettings.SPI_CLOCK_MAX))
            End If
        ElseIf MySettings.OPERATION_MODE = DeviceMode.SQI Then
            GUI.PrintConsole("Attempting to detect SPI device in SPI extended mode")
            If usb_dev.SQI_NOR_IF.DeviceInit() Then
                If usb_dev.HWBOARD = FCUSB_BOARD.Professional_PCB4 OrElse usb_dev.HWBOARD = FCUSB_BOARD.Mach1 Then
                    Connected_Event(usb_dev, MemoryType.SERIAL_QUAD, "SQI Flash", 131072)
                Else
                    Connected_Event(usb_dev, MemoryType.SERIAL_QUAD, "SQI Flash", 16384)
                End If
                GUI.PrintConsole(RM.GetString("spi_detected_sqi"))
            Else
                Select Case usb_dev.SQI_NOR_IF.MyFlashStatus
                    Case DeviceStatus.NotDetected
                        GUI.PrintConsole(RM.GetString("spi_unable_detect")) '"Unable to detect to SPI NOR Flash device"
                        GUI.SetStatus(RM.GetString("spi_not_detected")) '"Flash memory not detected on SPI NOR mode"
                    Case DeviceStatus.NotSupported
                        GUI.SetStatus(RM.GetString("mem_not_supported")) '"Flash memory detected but not found in Flash library"
                End Select
                Exit Sub
            End If
        ElseIf MySettings.OPERATION_MODE = DeviceMode.SPI_NAND Then
            GUI.PrintConsole(RM.GetString("spi_nand_attempt_detect"))
            If usb_dev.SPI_NAND_IF.DeviceInit Then
                Connected_Event(usb_dev, MemoryType.SERIAL_NAND, "SPI NAND Flash", 65536)
                GUI.PrintConsole(RM.GetString("spi_nand_detected"))
                GUI.PrintConsole(String.Format(RM.GetString("spi_set_clock"), GetSpiClockString(usb_dev)))
                usb_dev.USB_SPI_SETSPEED(GetSpiClock(usb_dev.HWBOARD, MySettings.SPI_CLOCK_MAX))
            Else
                Select Case usb_dev.SPI_NOR_IF.MyFlashStatus
                    Case DeviceStatus.NotDetected
                        Dim msg As String = RM.GetString("spi_nand_unable_to_detect")
                        GUI.PrintConsole(msg)
                        GUI.SetStatus(msg)
                    Case DeviceStatus.NotSupported
                        GUI.SetStatus(RM.GetString("mem_not_supported")) '"Flash memory detected but not found in Flash library"
                End Select
                Exit Sub
            End If
        ElseIf MySettings.OPERATION_MODE = DeviceMode.NOR_NAND Then
            GUI.PrintConsole(RM.GetString("ext_init")) 'Initializing EXT I/O hardware board
            Utilities.Sleep(250) 'Wait for IO board vcc to charge
            usb_dev.EXT_IF.DeviceInit()
            Select Case usb_dev.EXT_IF.MyFlashStatus
                Case DeviceStatus.Supported
                    GUI.SetStatus(RM.GetString("mem_flash_supported")) '"Flash device successfully detected and ready for operation"
                    If (usb_dev.EXT_IF.MyAdapter = MEM_PROTOCOL.NAND_X16) Then
                        If usb_dev.HWBOARD = FCUSB_BOARD.Mach1 Then
                            Connected_Event(usb_dev, MemoryType.NAND, "NAND X16 Flash", 524288)
                        Else
                            Connected_Event(usb_dev, MemoryType.NAND, "NAND X16 Flash", 65536)
                        End If
                    ElseIf (usb_dev.EXT_IF.MyAdapter = MEM_PROTOCOL.NAND_X8) Then
                        If usb_dev.HWBOARD = FCUSB_BOARD.Mach1 Then
                            Connected_Event(usb_dev, MemoryType.NAND, "NAND X8 Flash", 262144)
                        Else
                            Connected_Event(usb_dev, MemoryType.NAND, "NAND X8 Flash", 65536)
                        End If
                    ElseIf (usb_dev.EXT_IF.MyAdapter = MEM_PROTOCOL.FWH) Then
                        Connected_Event(usb_dev, MemoryType.FWH_NOR, "FWH Flash", 4096)
                    Else
                        Connected_Event(usb_dev, MemoryType.PARALLEL_NOR, "NOR Flash", 16384)
                    End If
                Case DeviceStatus.NotSupported
                    GUI.SetStatus(RM.GetString("mem_not_supported")) '"Flash memory detected but not found in Flash library"
                Case DeviceStatus.NotDetected
                    GUI.SetStatus(RM.GetString("ext_not_detected")) '"Flash device not detected in Extension I/O mode"
                Case DeviceStatus.ExtIoNotConnected
                    GUI.SetStatus(RM.GetString("ext_board_not_detected")) '"Unable to connect to the Extension I/O board"
                Case DeviceStatus.NotCompatible
                    GUI.SetStatus("Flash memory is not compatible with this FlashcatUSB programmer model")
            End Select
        ElseIf MySettings.OPERATION_MODE = DeviceMode.HyperFlash Then
            GUI.PrintConsole("Initializing HyperFlash device mode") 'Initializing EXT I/O hardware board
            Utilities.Sleep(250) 'Wait for IO board vcc to charge
            usb_dev.HF_IF.DeviceInit()
            Select Case usb_dev.HF_IF.MyFlashStatus
                Case DeviceStatus.Supported
                    Connected_Event(usb_dev, MemoryType.HYPERFLASH, "NOR Flash", 262144)
                Case DeviceStatus.NotSupported
                    GUI.SetStatus(RM.GetString("mem_not_supported")) '"Flash memory detected but not found in Flash library"
                Case DeviceStatus.NotDetected
                    GUI.SetStatus("HyperFlash device not detected")
            End Select
        ElseIf MySettings.OPERATION_MODE = DeviceMode.SPI_EEPROM Then
            SPIEEPROM_Configure(usb_dev, MySettings.SPI_EEPROM)
            Dim md As MemoryDeviceInstance = Connected_Event(usb_dev, MemoryType.SERIAL_NOR, "SPI EEPROM", 1024)
            If (Not usb_dev.SPI_NOR_IF.MyFlashDevice.ERASE_REQUIRED) Then
                md.GuiControl.AllowFullErase = False
            End If
            Utilities.Sleep(100) 'Wait for device to be configured
            GUI.PrintConsole(RM.GetString("spi_eeprom_cfg"))
        ElseIf MySettings.OPERATION_MODE = DeviceMode.I2C_EEPROM Then
            If MySettings.I2C_INDEX = 0 Then
                GUI.PrintConsole("No I2C EEPROM device selected") : Exit Sub
            End If
            usb_dev.I2C_IF.DeviceInit()
            GUI.PrintConsole(RM.GetString("i2c_attempt_detect"))
            GUI.PrintConsole(String.Format(RM.GetString("i2c_addr_byte"), Hex(MySettings.I2C_ADDRESS)))
            GUI.PrintConsole(String.Format(RM.GetString("i2c_eeprom_size"), Format(usb_dev.I2C_IF.DeviceSize, "#,###")))
            Select Case MySettings.I2C_SPEED
                Case I2C_SPEED_MODE._100kHz
                    GUI.PrintConsole(RM.GetString("i2c_protocol_speed") & ": 100kHz")
                Case I2C_SPEED_MODE._400kHz
                    GUI.PrintConsole(RM.GetString("i2c_protocol_speed") & ": 400kHz")
                Case I2C_SPEED_MODE._1MHz
                    GUI.PrintConsole(RM.GetString("i2c_protocol_speed") & ": 1MHz (Fm+)")
            End Select
            If usb_dev.I2C_IF.IsConnected() Then
                Connected_Event(usb_dev, MemoryType.SERIAL_I2C, "I2C EEPROM", 512)
                GUI.SetStatus(RM.GetString("i2c_detected")) '"I2C EEPROM detected and ready for operation"
                GUI.PrintConsole(RM.GetString("i2c_connected"))
            Else
                GUI.PrintConsole(RM.GetString("i2c_unable_to_connect"))
                GUI.SetStatus(RM.GetString("i2c_not_detected"))
            End If
        ElseIf MySettings.OPERATION_MODE = DeviceMode.Microwire Then
            GUI.PrintConsole("Connecting to Microwire EEPROM device")
            If usb_dev.MW_IF.DeviceInit() Then
                Connected_Event(usb_dev, MemoryType.SERIAL_MICROWIRE, "Microwire EEPROM", 256)
            End If
        ElseIf MySettings.OPERATION_MODE = DeviceMode.EPROM Then
            USBCLIENT.HW_BUSY = True
            GUI.PrintConsole(RM.GetString("ext_init"))
            If GUI IsNot Nothing Then
                AddHandler usb_dev.EXT_IF.SetProgress, AddressOf GUI.SetStatusPageProgress
            End If
            If usb_dev.EXT_IF.EPROM_Init() Then
                Dim mi As MemoryDeviceInstance = Connected_Event(usb_dev, MemoryType.OTP_EPROM, "EPROM", 16384) '8192
                mi.GuiControl.AllowFullErase = False
            Else
                GUI.SetStatus("EPROM device not detected")
            End If
            If GUI IsNot Nothing Then
                RemoveHandler usb_dev.EXT_IF.SetProgress, AddressOf GUI.SetStatusPageProgress
                GUI.SetStatusPageProgress(100)
            End If
            USBCLIENT.HW_BUSY = False
        ElseIf MySettings.OPERATION_MODE = DeviceMode.SINGLE_WIRE Then
            GUI.PrintConsole("Connecting to Single-Wire EEPROM device")
            If usb_dev.SWI_IF.DeviceInit() Then
                Dim mi As MemoryDeviceInstance = Connected_Event(usb_dev, MemoryType.SERIAL_SWI, "Single-wire EEPROM", 128)
                mi.GuiControl.AllowFullErase = False
                mi.VendorMenu = New vendor_microchip_at21(usb_dev)
            End If
        Else '(OTHER MODES)
        End If
    End Sub

    Private Function Connected_Event(usb_dev As FCUSB_DEVICE, mem_type As MemoryType, tab_name As String, block_size As UInt32) As MemoryDeviceInstance
        Try
            Dim access As MemControl_v2.access_mode = MemControl_v2.access_mode.Writable
            Utilities.Sleep(150) 'Some devices (such as Spansion 128mbit devices) need a delay here
            Dim mem_dev As MemoryDeviceUSB = Nothing
            Select Case mem_type
                Case MemoryType.SERIAL_NOR
                    mem_dev = usb_dev.SPI_NOR_IF
                Case MemoryType.SERIAL_NAND
                    mem_dev = usb_dev.SPI_NAND_IF
                Case MemoryType.PARALLEL_NOR
                    mem_dev = usb_dev.EXT_IF
                    If usb_dev.EXT_IF.MyFlashDevice.GetType Is GetType(OTP_EPROM) Then
                        If Not DirectCast(usb_dev.EXT_IF.MyFlashDevice, OTP_EPROM).IS_BLANK Then
                            access = MemControl_v2.access_mode.ReadOnly
                        Else
                            access = MemControl_v2.access_mode.WriteOnce
                        End If
                        'FOR BETA, ALLOW USER TO ATTEMPT TO WRITE TO A NON BLANK DEVICE
                        access = MemControl_v2.access_mode.WriteOnce
                    End If
                Case MemoryType.NAND
                    mem_dev = usb_dev.EXT_IF
                Case MemoryType.SERIAL_I2C
                    mem_dev = usb_dev.I2C_IF
                Case MemoryType.SERIAL_MICROWIRE
                    mem_dev = usb_dev.MW_IF
                Case MemoryType.SERIAL_QUAD
                    mem_dev = usb_dev.SQI_NOR_IF
                Case MemoryType.FWH_NOR
                    mem_dev = usb_dev.EXT_IF
                Case MemoryType.OTP_EPROM
                    mem_dev = usb_dev.EXT_IF
                Case MemoryType.HYPERFLASH
                    mem_dev = usb_dev.HF_IF
                Case MemoryType.SERIAL_SWI
                    mem_dev = usb_dev.SWI_IF
            End Select
            Dim dev_inst As MemoryDeviceInstance = MEM_IF.Add(usb_dev, mem_type, mem_dev.DeviceName, mem_dev.DeviceSize)
            dev_inst.PreferredBlockSize = block_size
            usb_dev.ATTACHED.Add(dev_inst)
            If GUI IsNot Nothing Then
                AddHandler dev_inst.PrintConsole, AddressOf GUI.PrintConsole
                AddHandler dev_inst.SetStatus, AddressOf GUI.SetStatus
                AddHandler mem_dev.SetProgress, AddressOf dev_inst.GuiControl.SetProgress
                Dim newTab As New TabPage("  " & tab_name & "  ")
                newTab.Tag = dev_inst
                dev_inst.GuiControl.Width = newTab.Width
                dev_inst.GuiControl.Height = newTab.Height
                dev_inst.GuiControl.Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top Or AnchorStyles.Bottom
                dev_inst.GuiControl.InitMemoryDevice(usb_dev, dev_inst.Name, dev_inst.Size, access)
                newTab.Controls.Add(dev_inst.GuiControl)
                GUI.AddTab(newTab)
                dev_inst.GuiControl.SetupLayout()
                GUI.OnNewDeviceConnected(usb_dev)
            End If
            Return dev_inst
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    Public Function JTAG_Connect_CFI(ByVal usb_dev As FCUSB_DEVICE, ByVal base_address As UInt32) As MemoryDeviceInstance
        Try
            WriteConsole(String.Format(RM.GetString("jtag_cfi_attempt_detect"), Hex(base_address).PadLeft(8, "0")))
            If usb_dev.JTAG_IF.CFI_Detect(base_address) Then
                Dim dev_inst As MemoryDeviceInstance = MEM_IF.Add(usb_dev, MemoryType.JTAG_CFI, usb_dev.JTAG_IF.CFI_GetFlashName, usb_dev.JTAG_IF.CFI_GetFlashSize)
                dev_inst.PreferredBlockSize = 16384
                usb_dev.ATTACHED.Add(dev_inst)
                If GUI IsNot Nothing Then
                    AddHandler dev_inst.PrintConsole, AddressOf GUI.PrintConsole
                    AddHandler dev_inst.SetStatus, AddressOf GUI.SetStatus
                    Dim newTab As New TabPage("  " & dev_inst.GetTypeString() & "  ")
                    newTab.Tag = dev_inst
                    dev_inst.GuiControl.Width = newTab.Width
                    dev_inst.GuiControl.Height = newTab.Height
                    dev_inst.GuiControl.Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top Or AnchorStyles.Bottom
                    dev_inst.GuiControl.InitMemoryDevice(usb_dev, dev_inst.Name, dev_inst.Size, MemControl_v2.access_mode.Writable)
                    dev_inst.GuiControl.AllowFullErase = True
                    newTab.Controls.Add(dev_inst.GuiControl)
                    GUI.AddTab(newTab)
                    GUI.OnNewDeviceConnected(usb_dev)
                End If
                Return dev_inst
            Else
                WriteConsole(RM.GetString("jtag_cfi_no_detect"))
            End If
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    Public Function JTAG_Connect_BSDL(ByVal usb_dev As FCUSB_DEVICE) As MemoryDeviceInstance
        Dim flash_name As String = usb_dev.JTAG_IF.BoundaryScan_DeviceName()
        Dim flash_size As Long = usb_dev.JTAG_IF.BoundaryScan_DeviceSize()
        Dim dev_inst As MemoryDeviceInstance = MEM_IF.Add(usb_dev, MemoryType.JTAG_BSDL, flash_name, flash_size)
        dev_inst.PreferredBlockSize = 4096
        usb_dev.ATTACHED.Add(dev_inst)
        If GUI IsNot Nothing Then
            AddHandler dev_inst.PrintConsole, AddressOf GUI.PrintConsole
            AddHandler dev_inst.SetStatus, AddressOf GUI.SetStatus
            Dim newTab As New TabPage("  " & dev_inst.GetTypeString() & "  ")
            newTab.Tag = dev_inst
            dev_inst.GuiControl.Width = newTab.Width
            dev_inst.GuiControl.Height = newTab.Height
            dev_inst.GuiControl.Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top Or AnchorStyles.Bottom
            dev_inst.GuiControl.InitMemoryDevice(usb_dev, dev_inst.Name, dev_inst.Size, MemControl_v2.access_mode.Writable)
            dev_inst.GuiControl.AllowFullErase = True
            newTab.Controls.Add(dev_inst.GuiControl)
            GUI.AddTab(newTab)
            GUI.OnNewDeviceConnected(usb_dev)
        End If
        Return dev_inst
    End Function

    Public Function JTAG_Connect_SPI(ByVal usb_dev As FCUSB_DEVICE, ByVal hw_type As Integer) As MemoryDeviceInstance
        Try
            WriteConsole(RM.GetString("jtag_spi_attempt_detect"))
            If usb_dev.JTAG_IF.SPI_Detect(hw_type) Then
                Dim dev_inst As MemoryDeviceInstance = MEM_IF.Add(usb_dev, MemoryType.JTAG_SPI, usb_dev.JTAG_IF.SPI_Part.NAME, usb_dev.JTAG_IF.SPI_Part.FLASH_SIZE)
                dev_inst.PreferredBlockSize = 16384
                usb_dev.ATTACHED.Add(dev_inst)
                If GUI IsNot Nothing Then
                    AddHandler dev_inst.PrintConsole, AddressOf GUI.PrintConsole
                    AddHandler dev_inst.SetStatus, AddressOf GUI.SetStatus
                    Dim newTab As New TabPage("  " & dev_inst.GetTypeString() & "  ")
                    newTab.Tag = dev_inst
                    dev_inst.GuiControl.Width = newTab.Width
                    dev_inst.GuiControl.Height = newTab.Height
                    dev_inst.GuiControl.Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top Or AnchorStyles.Bottom
                    dev_inst.GuiControl.InitMemoryDevice(usb_dev, dev_inst.Name, dev_inst.Size, MemControl_v2.access_mode.Writable)
                    dev_inst.GuiControl.AllowFullErase = True
                    newTab.Controls.Add(dev_inst.GuiControl)
                    Dim CHIP_ID As UInt32 = (usb_dev.JTAG_IF.SPI_Part.ID1 << 16) Or (usb_dev.JTAG_IF.SPI_Part.ID2)
                    GUI.AddTab(newTab)
                    GUI.OnNewDeviceConnected(usb_dev)
                End If
                Return dev_inst
            Else
                WriteConsole(RM.GetString("jtag_spi_no_detect")) '"Error: unable to detect SPI flash device over JTAG"
            End If
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    Public Function JTAG_Connect_DMA(ByVal usb_dev As FCUSB_DEVICE, ByVal base_address As UInt32, ByVal dram_size As UInt32) As MemoryDeviceInstance
        Try
            Dim dev_inst As MemoryDeviceInstance = MEM_IF.Add(usb_dev, MemoryType.JTAG_DMA_RAM, "DRAM", dram_size)
            dev_inst.PreferredBlockSize = 16384
            dev_inst.BaseAddress = base_address
            usb_dev.ATTACHED.Add(dev_inst)
            If GUI IsNot Nothing Then
                AddHandler dev_inst.PrintConsole, AddressOf GUI.PrintConsole
                AddHandler dev_inst.SetStatus, AddressOf GUI.SetStatus
                Dim newTab As New TabPage("  " & dev_inst.GetTypeString() & "  ")
                newTab.Tag = dev_inst
                dev_inst.GuiControl.Width = newTab.Width
                dev_inst.GuiControl.Height = newTab.Height
                dev_inst.GuiControl.Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top Or AnchorStyles.Bottom
                dev_inst.GuiControl.InitMemoryDevice(usb_dev, dev_inst.Name, dev_inst.Size, MemControl_v2.access_mode.Writable, dev_inst.BaseAddress)
                dev_inst.GuiControl.AllowFullErase = False
                newTab.Controls.Add(dev_inst.GuiControl)
                GUI.AddTab(newTab)
                GUI.OnNewDeviceConnected(usb_dev)
            End If
            Return dev_inst
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    Private Function DFU_Connected_Event(ByVal usb_dev As FCUSB_DEVICE) As MemoryDeviceInstance
        Try
            Dim DevSize As UInt32 = usb_dev.DFU_IF.GetFlashSize
            Dim dev_inst As MemoryDeviceInstance = MEM_IF.Add(usb_dev, MemoryType.DFU_MODE, "AVR Firmware", DevSize)
            dev_inst.GuiControl = Nothing
            usb_dev.ATTACHED.Add(dev_inst)
            If GUI IsNot Nothing Then
                AddHandler dev_inst.PrintConsole, AddressOf GUI.PrintConsole
                AddHandler dev_inst.SetStatus, AddressOf GUI.SetStatus
                Dim newTab As New TabPage("  " & dev_inst.GetTypeString() & "  ")
                newTab.Tag = dev_inst
                Dim DfuApp As New DfuControl
                DfuApp.Width = newTab.Width
                DfuApp.Height = newTab.Height
                DfuApp.Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top Or AnchorStyles.Bottom
                DfuApp.LoadWindow(usb_dev)
                newTab.Controls.Add(DfuApp)
                GUI.AddTab(newTab)
                GUI.OnNewDeviceConnected(usb_dev)
            End If
            Return dev_inst
        Catch ex As Exception
        End Try
        Return Nothing
    End Function

    Public Sub OnDeviceUpdateProgress(ByVal percent As Integer, ByRef device As FCUSB_DEVICE)
        If GUI IsNot Nothing Then
            GUI.SetStatusPageProgress(percent)
        End If
    End Sub
    'Makes sure the current firmware is installed
    Private Function FirmwareCheck(fw_str As String, current_version As Single) As Boolean
        Dim AvrVerSng As Single = Utilities.StringToSingle(fw_str)
        If (Not AvrVerSng = current_version) Then
            GUI.PrintConsole(String.Format(RM.GetString("sw_requires_fw"), current_version.ToString))
            SetStatus(RM.GetString("fw_out_of_date"))
            Return False
        End If
        Return True
    End Function

#End Region

#Region "FlashcatUSB Pro and Mach1"

    Private Sub FCUSBPRO_Bootloader(ByVal usb_dev As FCUSB_DEVICE, ByVal board_firmware As String)
        Dim fw_ver As Single = 0
        If usb_dev.HWBOARD = FCUSB_BOARD.Mach1 Then
            fw_ver = MACH1_PCB2_FW
        ElseIf usb_dev.HWBOARD = FCUSB_BOARD.Professional_PCB4 Then
            fw_ver = PRO_PCB4_FW
        End If
        USBCLIENT.HW_BUSY = True
        GUI.PrintConsole(RM.GetString("connected_bl_mode"))
        GUI.UpdateStatusMessage(RM.GetString("device_mode"), RM.GetString("bootloader_mode"))
        Application.DoEvents()
        GUI.SetStatus(RM.GetString("fw_update_performing")) 'Performing firmware unit update
        Utilities.Sleep(500)
        Dim Current_fw() As Byte = Utilities.GetResourceAsBytes(board_firmware)
        GUI.SetStatus(String.Format(RM.GetString("fw_update_starting"), Format(Current_fw.Length, "#,###")))
        Dim result As Boolean = usb_dev.SAM3U_FirmwareUpdate(Current_fw, fw_ver)
        GUI.SetStatusPageProgress(100)
        If result Then
            WriteConsole("Firmware update was a success!")
        Else
            GUI.SetStatus(RM.GetString("fw_update_error"))
        End If
        USBCLIENT.HW_BUSY = False
    End Sub

    Private Sub FCUSBPRO_SetDeviceVoltage(ByVal usb_dev As FCUSB_DEVICE, Optional silent As Boolean = False)
        Dim console_message As String
        If MySettings.VOLT_SELECT = Voltage.V1_8 Then
            console_message = String.Format(RM.GetString("voltage_set_to"), "1.8V")
            usb_dev.USB_VCC_1V8()
        Else
            MySettings.VOLT_SELECT = Voltage.V3_3
            console_message = String.Format(RM.GetString("voltage_set_to"), "3.3V")
            usb_dev.USB_VCC_3V()
        End If
        If Not silent Then WriteConsole(console_message)
        Utilities.Sleep(200)
    End Sub

    Private Sub FCUSBPRO_RebootToBootloader(usb_dev As FCUSB_DEVICE)
        GUI.SetStatus(RM.GetString("fw_update_available"))
        Utilities.Sleep(2000)
        If usb_dev.HWBOARD = FCUSB_BOARD.Professional_PCB4 Then
            usb_dev.USB_CONTROL_MSG_OUT(USBREQ.FW_REBOOT, Nothing, &HFFFFFFFFUI) 'Removes firmware version
        ElseIf usb_dev.HWBOARD = FCUSB_BOARD.Mach1 Then
            usb_dev.USB_CONTROL_MSG_OUT(USBREQ.FW_REBOOT, Nothing, &HFFFFFFFFUI) 'Removes firmware version
        End If
        usb_dev.Disconnect()
        Application.DoEvents()
        Utilities.Sleep(300)
    End Sub

    Public Sub FCUSBPRO_Update_Logic()
        Try
            For Each device In USBCLIENT.FCUSB
                If device.IS_CONNECTED Then
                    If device.HWBOARD = FCUSB_BOARD.Professional_PCB4 Then
                        WriteConsole("Updating all CPLD logic")
                        FCUSBPRO_PCB4_Init(device)
                        SetStatus("CPLD logic successfully updated")
                        Application.DoEvents()
                    ElseIf device.HWBOARD = FCUSB_BOARD.Mach1 Then
                        WriteConsole("Updating all FPGA logic")
                        FCUSBPRO_Mach1_Init(device)
                        SetStatus("FPGA logic successfully updated")
                        Application.DoEvents()
                    End If
                End If
            Next
        Catch ex As Exception
        End Try
    End Sub
    'PCB 4.x only
    Public Function FCUSBPRO_PCB4_Init(usb_dev As FCUSB_DEVICE) As Boolean
        usb_dev.USB_VCC_OFF()
        If (Not usb_dev.HWBOARD = FCUSB_BOARD.Professional_PCB4) Then Return False
        Dim cpld32 As UInt32 = usb_dev.LOGIC_GetVersion()
        If IS_DEBUG_VER Then
            Return True 'We dont want to update the CPLD
        End If
        Dim mode_needed As CPLD_MODE = CPLD_MODE.NotSelected
        If MySettings.OPERATION_MODE = DeviceMode.I2C_EEPROM Then
            mode_needed = CPLD_MODE.I2C
            If (Not MySettings.VOLT_SELECT = USB.Voltage.V3_3) Then
                MySettings.VOLT_SELECT = Voltage.V3_3
            End If
        ElseIf MySettings.OPERATION_MODE = DeviceMode.JTAG Then
            mode_needed = CPLD_MODE.JTAG
        ElseIf MySettings.OPERATION_MODE = DeviceMode.SQI Then
            If MySettings.VOLT_SELECT = Voltage.V1_8 Then
                mode_needed = CPLD_MODE.QSPI_1V8
            Else
                mode_needed = CPLD_MODE.QSPI_3V
            End If
        Else
            If MySettings.VOLT_SELECT = Voltage.V1_8 Then
                mode_needed = CPLD_MODE.SPI_1V8
            ElseIf MySettings.VOLT_SELECT = Voltage.V3_3 Then
                mode_needed = CPLD_MODE.SPI_3V
            Else
                MySettings.VOLT_SELECT = Voltage.V3_3
                mode_needed = CPLD_MODE.SPI_3V
            End If
        End If
        Dim svf_data() As Byte = Nothing
        Dim svf_code As UInt32 = 0
        If mode_needed = CPLD_MODE.I2C And (Not cpld32 = CPLD_I2C) Then
            svf_data = Utilities.GetResourceAsBytes("PCB4_I2C.svf")
            svf_code = CPLD_I2C
        ElseIf mode_needed = CPLD_MODE.JTAG And (Not cpld32 = CPLD_JTAG) Then
            svf_data = Utilities.GetResourceAsBytes("JTAG_3V3.svf")
            svf_code = CPLD_JTAG
        ElseIf mode_needed = CPLD_MODE.QSPI_3V And (Not cpld32 = CPLD_QSPI_3V3) Then
            svf_data = Utilities.GetResourceAsBytes("PCB4_QUAD_3V3.svf")
            svf_code = CPLD_QSPI_3V3
        ElseIf mode_needed = CPLD_MODE.QSPI_1V8 And (Not cpld32 = CPLD_QSPI_1V8) Then
            svf_data = Utilities.GetResourceAsBytes("PCB4_QUAD_1V8.svf")
            svf_code = CPLD_QSPI_1V8
        ElseIf mode_needed = CPLD_MODE.SPI_1V8 And (Not cpld32 = CPLD_SPI_1V8) Then
            svf_data = Utilities.GetResourceAsBytes("PCB4_1V8.svf")
            svf_code = CPLD_SPI_1V8
        ElseIf mode_needed = CPLD_MODE.SPI_3V And (Not cpld32 = CPLD_SPI_3V3) Then
            svf_data = Utilities.GetResourceAsBytes("PCB4_3V3.svf")
            svf_code = CPLD_SPI_3V3
        End If
        If svf_data IsNot Nothing Then
            ProgramSVF(usb_dev, svf_data, svf_code)
            Return False 'Stop
        End If
        Return True 'Continue
    End Function

    Public Function FCUSBPRO_PCB5_Init(usb_dev As FCUSB_DEVICE) As Boolean
        'SPI programming here


        Return True 'Continue
    End Function

    Private Sub onCpldUpdateProgress(ByVal percent As Integer)
        Static LastPercent As Integer = -1
        If LastPercent = percent Then Exit Sub
        If GUI IsNot Nothing Then
            GUI.SetStatusPageProgress(percent)
        Else
            Console_UpdateProgress(percent)
        End If
    End Sub

    Private Enum CPLD_MODE
        NotSelected 'Default
        SPI_3V 'Standard GPIO/SPI @ 3.3V
        SPI_1V8 'Standard GPIO/SPI @ 1.8V
        QSPI_3V
        QSPI_1V8
        I2C 'I2C only mode @ 3.3V
        JTAG 'JTAG mode @ 3.3V
        NAND_1V8 'NAND mode @ 1.8V
        NAND_3V3 'NAND mode @ 3.3V
        HF_1V8 'HyperFlash @ 1.8V
        HF_3V3 'HyperFlash @ 3.3V
    End Enum

    Public Function FCUSBPRO_Mach1_Init(usb_dev As FCUSB_DEVICE) As Boolean
        If Not usb_dev.HWBOARD = FCUSB_BOARD.Mach1 Then Return False
        Dim cpld32 As UInt32 = usb_dev.LOGIC_GetVersion()
        FCUSBPRO_SetDeviceVoltage(usb_dev) 'Power on CPLD
        If IS_DEBUG_VER Then Return True 'We dont want to update the CPLD
        Dim mode_needed As CPLD_MODE = CPLD_MODE.NotSelected
        If MySettings.OPERATION_MODE = DeviceMode.NOR_NAND Then
            mode_needed = CPLD_MODE.NAND_3V3
            If MySettings.VOLT_SELECT = Voltage.V1_8 Then mode_needed = CPLD_MODE.NAND_1V8
        ElseIf MySettings.OPERATION_MODE = DeviceMode.HyperFlash Then
            mode_needed = CPLD_MODE.HF_3V3
            If MySettings.VOLT_SELECT = Voltage.V1_8 Then mode_needed = CPLD_MODE.HF_1V8
        ElseIf MySettings.OPERATION_MODE = DeviceMode.SQI Then
            mode_needed = CPLD_MODE.QSPI_3V
            If MySettings.VOLT_SELECT = Voltage.V1_8 Then mode_needed = CPLD_MODE.QSPI_1V8
        Else Return False 'Mach1 must be NAND or HyperFlash
        End If
        Dim svf_data() As Byte = Nothing
        Dim svf_code As UInt32 = 0
        If Utilities.StringToSingle(usb_dev.FW_VERSION()) = MACH1_PCB2_FW Then
            If MySettings.VOLT_SELECT = Voltage.V1_8 And (Not cpld32 = MACH1_FGPA_1V8) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_PCB2_1V8.svf")
                svf_code = MACH1_FGPA_1V8
            ElseIf MySettings.VOLT_SELECT = Voltage.V3_3 And (Not cpld32 = MACH1_FGPA_3V3) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_PCB2_3V3.svf")
                svf_code = MACH1_FGPA_3V3
            End If
        Else
            If mode_needed = CPLD_MODE.NAND_1V8 And (Not cpld32 = CPLD_NAND_1V8) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_NAND_1V8.svf")
                svf_code = CPLD_NAND_1V8
            ElseIf mode_needed = CPLD_MODE.NAND_3V3 And (Not cpld32 = CPLD_NAND_3V3) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_NAND_3V3.svf")
                svf_code = CPLD_NAND_3V3
            ElseIf mode_needed = CPLD_MODE.HF_1V8 And (Not cpld32 = CPLD_HF_1V8) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_HF_1V8.svf")
                svf_code = CPLD_HF_1V8
            ElseIf mode_needed = CPLD_MODE.HF_3V3 And (Not cpld32 = CPLD_HF_3V3) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_HF_3V3.svf")
                svf_code = CPLD_HF_3V3
            ElseIf mode_needed = CPLD_MODE.QSPI_3V And (Not cpld32 = CPLD_QSPI_3V3) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_QSPI_3V3.svf")
                svf_code = CPLD_QSPI_3V3
            ElseIf mode_needed = CPLD_MODE.QSPI_1V8 And (Not cpld32 = CPLD_QSPI_1V8) Then
                svf_data = Utilities.GetResourceAsBytes("MACH1_QSPI_1V8.svf")
                svf_code = CPLD_QSPI_1V8
            End If
        End If
        If (svf_data IsNot Nothing) Then
            ProgramSVF(usb_dev, svf_data, svf_code)
            Return False 'Stop
        End If
        Return True
    End Function

    Private Sub ProgramSVF(usb_dev As FCUSB_DEVICE, svf_data() As Byte, svf_code As UInt32)
        Dim logic_type As String = "CPLD"
        Try : USBCLIENT.HW_BUSY = True
            If (usb_dev.HWBOARD = FCUSB_BOARD.Mach1) Then
                If Utilities.StringToSingle(usb_dev.FW_VERSION()) = MACH1_PCB2_FW Then
                    logic_type = "FPGA"
                End If
            End If
            SetStatus("Programming on board " & logic_type & " with new logic")
            usb_dev.USB_VCC_OFF()
            Utilities.Sleep(1000)
            If Not usb_dev.JTAG_IF.Init(True) Then
                SetStatus("Error: unable to connect to on board " & logic_type & " via JTAG")
                Exit Sub
            End If
            usb_dev.USB_VCC_ON()
            Dim svf_file() As String = Utilities.Bytes.ToCharStringArray(svf_data)
            RemoveHandler usb_dev.JTAG_IF.JSP.Progress, AddressOf onCpldUpdateProgress
            AddHandler usb_dev.JTAG_IF.JSP.Progress, AddressOf onCpldUpdateProgress
            WriteConsole("Programming SVF data into Logic device")
            usb_dev.LOGIC_SetVersion(&HFFFFFFFFUI)
            Dim result As Boolean = usb_dev.JTAG_IF.JSP.RunFile_SVF(svf_file)
            onCpldUpdateProgress(100)
            If result Then
                WriteConsole("Logic device successfully programmed")
                SetStatus("" & logic_type & " successfully programmed!")
                usb_dev.LOGIC_SetVersion(svf_code)
            Else
                WriteConsole("Error: logic device failed programming")
                SetStatus("Error, unable to program in-circuit " & logic_type & "")
                Exit Sub
            End If
            Utilities.Sleep(250)
            usb_dev.USB_CONTROL_MSG_OUT(USBREQ.FW_REBOOT, Nothing, 0) 'We need to reboot to clean up USB memory
        Catch ex As Exception
            SetStatus("Exception in programming " & logic_type)
        Finally
            USBCLIENT.HW_BUSY = False
        End Try
    End Sub

#End Region

#Region "SPI Logic Programming"
    Private sel_usb_dev As FCUSB_DEVICE

    Public USB_tx_buffer(65535) As Byte
    Public USB_rx_buffer(65535) As Byte

    Private callback_var As New PROGRESS_CALLBACK(AddressOf callback)
    Private writeCS_var As New WRITE_CALLBACK(AddressOf writeCS)
    Private writeTRST_var As New WRITE_CALLBACK(AddressOf writeTRST)
    Private runClocks_var As New WRITE_CALLBACK(AddressOf runClocks)
    Private SPI_WriteData_var As New SPI_WRITE_CALLBACK(AddressOf SPI_WriteData)
    Private SPI_ReadData_var As New SPI_READ_CALLBACK(AddressOf SPI_ReadData)
    Private SPI_WriteRead_var As New SPI_WRITEREAD_CALLBACK(AddressOf SPI_WriteRead)

    Private Sub Mach1Dev(usb_dev As FCUSB_DEVICE)

        'Dim erase_svf As String = "SMC_FPGA_SMC_FPGA.svf"
        'Dim svf_data() As Byte = Utilities.FileIO.ReadBytes(erase_svf)
        'ProgramSVF(usb_dev, svf_data, &HFFFFFFFFUI)

        'Dim erase_svf As String = "LCMXO2-4000HC_ERASE_NEW.svf"
        'Dim svf_data() As Byte = Utilities.FileIO.ReadBytes(erase_svf)
        'ProgramSVF(usb_dev, svf_data, &HFFFFFFFFUI)

        'Dim erase_svf As String = "LCMXO2-4000HC_FEATROW.svf"
        'Dim svf_data() As Byte = Utilities.FileIO.ReadBytes(erase_svf)
        'ProgramSVF(usb_dev, svf_data, &HFFFFFFFFUI)

        usb_dev.USB_VCC_3V()

        sel_usb_dev = usb_dev
        Dim result As Boolean = usb_dev.USB_CONTROL_MSG_OUT(USBREQ.SPI_INIT, Nothing, 8000000)
        Utilities.Sleep(50)

        Dim sea_filename As String = "SMC_FPGA_SMC_FPGA_algo.sea"
        Dim sed_filename As String = "SMC_FPGA_SMC_FPGA_data.sed"

        Dim r As Integer
        r = SSPIEm_preset(sea_filename, sed_filename)
        SetCallback_write(writeCS_var, writeTRST_var, runClocks_var)
        SetCallback_spi(SPI_WriteData_var, SPI_ReadData_var, SPI_WriteRead_var)
        SetBuffer(USB_tx_buffer(0), USB_rx_buffer(0))

        SetCallback(callback_var)

        r = SSPIEm(&HFFFFFFFFUI)
        Dim rstr As String = EM_getCodeStr(r)
        Beep()

    End Sub

    Private Function EM_getCodeStr(code_result As Integer) As String
        Dim mess As String = "unknown"
        Select Case code_result
            Case SSPIEm_Code.Succeed
                mess = "Succeed"
            Case SSPIEm_Code.Process_Failed
                mess = "Process Failed"
            Case SSPIEm_Code.Init_Algo_Failed
                mess = "Initialize Alogrithm Failed"
            Case SSPIEm_Code.Init_Data_Failed
                mess = "Initialize Data Failed"
            Case SSPIEm_Code.Version_not_support
                mess = "Version Not Supported"
            Case SSPIEm_Code.Header_Checksum_Mismatch
                mess = "Header Checksum Mismatch"
            Case SSPIEm_Code.Init_SPI_Port_Failed
                mess = "Initialize SPI Port Failed"
            Case SSPIEm_Code.Init_Failed
                mess = "Initialize Failed"
            Case SSPIEm_Code.Algorithm_Error
                mess = "Algorithm Error"
            Case SSPIEm_Code.Data_Error
                mess = "Data Error"
            Case SSPIEm_Code.Hardware_Error
                mess = "Hardware Error"
            Case SSPIEm_Code.Verification_Error
                mess = "Verification Error"
        End Select
        Return mess
    End Function

    Public Function writeCS(value As Byte) As Boolean
        Dim success As Boolean
        If value = &H1 Then
            success = sel_usb_dev.USB_CONTROL_MSG_OUT(USBREQ.SPI_SS_DISABLE)
        ElseIf value = &H0 Then
            success = sel_usb_dev.USB_CONTROL_MSG_OUT(USBREQ.SPI_SS_ENABLE)
        End If
        Utilities.Sleep(5)
        Return success
    End Function

    Public Sub callback(d As Integer)
        Debug.WriteLine(d)
    End Sub

    Public Sub writeTRST(value As Byte)
        'This function isn't used on MachXo2 chip.
        Console.WriteLine(" write TRST" & Str(value))
    End Sub

    Public Sub runClocks(value As Byte)
        Console.WriteLine(" run Clocks" & Str(value))
        'This function isn't used on MachXo2 chip.
    End Sub

    Public Function SPI_WriteData(NumBytesToWrite As UInteger) As Integer
        Dim tx_buffer(NumBytesToWrite - 1) As Byte
        Array.Copy(USB_tx_buffer, 0, tx_buffer, 0, NumBytesToWrite)
        Dim Success As Boolean = sel_usb_dev.USB_SETUP_BULKOUT(USBREQ.SPI_WR_DATA, Nothing, tx_buffer, tx_buffer.Length)
        Utilities.Sleep(2)
        Return 0 'No error
    End Function

    Public Function SPI_ReadData(NumBytesToRead As UInteger, ByRef NumBytesReturned As UInteger) As Integer
        Dim rx_buffer(NumBytesToRead - 1) As Byte
        NumBytesReturned = 0 'NumBytesToRead
        Dim Success As Boolean = sel_usb_dev.USB_SETUP_BULKIN(USBREQ.SPI_RD_DATA, Nothing, rx_buffer, rx_buffer.Length)
        If Success Then
            Array.Copy(rx_buffer, 0, USB_rx_buffer, 0, NumBytesToRead)
            NumBytesReturned = NumBytesToRead
            Return 0 'No error
        Else
            NumBytesToRead = 0
            Return -1
        End If
    End Function

    Public Function SPI_WriteRead(dwNumBytesToWrite As UInteger, ByRef NumBytesReturned As UInteger) As Integer
        'dwNumBytesToWrite is the number of byte to write
        'NumBytesReturned is the number of byte read
        NumBytesReturned = dwNumBytesToWrite
        'Dim i As Integer
        'For i = 0 To dwNumBytesToWrite - 1
        '    Console.Write(Str(USB_tx_buffer(i)) + " ")
        'Next
        'Console.WriteLine(" ")
        'Console.Write(" write read")
        Return 0
    End Function
#End Region

End Module
